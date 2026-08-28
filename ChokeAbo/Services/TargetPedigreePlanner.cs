using FFXIVClientStructs.FFXIV.Client.Game;

namespace ChokeAbo.Services;

public enum ChocoboFormKind
{
    Proof,
    Fledgling,
    Retired,
    CoveringPermission,
}

public enum ChocoboSex
{
    Unknown,
    Male,
    Female,
}

public readonly record struct ChocoboInventoryForm(
    ChocoboFormKind Kind,
    uint ItemId,
    string ItemName,
    int Pedigree,
    ChocoboSex Sex,
    uint Capacity,
    uint Quantity,
    InventoryType Container,
    int Slot)
{
    public bool HasPositiveCapacity => Kind != ChocoboFormKind.Retired || Capacity > 0;

    public static bool TryDecode(InventoryItemSnapshot item, out ChocoboInventoryForm form)
    {
        form = default;
        if (item.ItemId == ChocoboInventoryModel.ProofOfCoveringItemId)
        {
            form = new ChocoboInventoryForm(
                ChocoboFormKind.Proof,
                item.ItemId,
                item.ItemName,
                0,
                ChocoboSex.Unknown,
                item.Condition,
                item.Quantity,
                item.Container,
                item.Slot);
            return true;
        }

        if (!TryDecodeRange(
                item.ItemId,
                ChocoboInventoryModel.FirstFledglingItemId,
                ChocoboInventoryModel.FirstFemaleFledglingItemId,
                ChocoboInventoryModel.LastFledglingItemId,
                out var pedigree,
                out var sex))
        {
            if (TryDecodeRange(
                    item.ItemId,
                    ChocoboInventoryModel.FirstRetiredItemId,
                    ChocoboInventoryModel.FirstFemaleRetiredItemId,
                    ChocoboInventoryModel.LastRetiredItemId,
                    out pedigree,
                    out sex))
            {
                form = Create(ChocoboFormKind.Retired, item, pedigree, sex);
                return true;
            }

            if (!TryDecodeRange(
                    item.ItemId,
                    ChocoboInventoryModel.FirstCoveringPermissionItemId,
                    ChocoboInventoryModel.FirstFemaleCoveringPermissionItemId,
                    ChocoboInventoryModel.LastCoveringPermissionItemId,
                    out pedigree,
                    out sex))
            {
                return false;
            }

            form = Create(ChocoboFormKind.CoveringPermission, item, pedigree, sex);
            return true;
        }

        form = Create(ChocoboFormKind.Fledgling, item, pedigree, sex);
        return true;
    }

    private static ChocoboInventoryForm Create(
        ChocoboFormKind kind,
        InventoryItemSnapshot item,
        int pedigree,
        ChocoboSex sex)
        => new(
            kind,
            item.ItemId,
            item.ItemName,
            pedigree,
            sex,
            item.Condition,
            item.Quantity,
            item.Container,
            item.Slot);

    private static bool TryDecodeRange(
        uint itemId,
        uint firstMaleItemId,
        uint firstFemaleItemId,
        uint lastItemId,
        out int pedigree,
        out ChocoboSex sex)
    {
        pedigree = 0;
        sex = ChocoboSex.Unknown;
        if (itemId < firstMaleItemId || itemId > lastItemId)
            return false;

        var female = itemId >= firstFemaleItemId;
        pedigree = (int)(itemId - (female ? firstFemaleItemId : firstMaleItemId)) + 1;
        sex = female ? ChocoboSex.Female : ChocoboSex.Male;
        return pedigree is >= 1 and <= 9;
    }
}

public static class ChocoboInventoryModel
{
    public const uint ProofOfCoveringItemId = 9560;
    public const uint FirstFledglingItemId = 9561;
    public const uint FirstFemaleFledglingItemId = 9570;
    public const uint LastFledglingItemId = 9578;
    public const uint FirstRetiredItemId = 9579;
    public const uint FirstFemaleRetiredItemId = 9588;
    public const uint LastRetiredItemId = 9596;
    public const uint FirstCoveringPermissionItemId = 9597;
    public const uint FirstFemaleCoveringPermissionItemId = 9606;
    public const uint LastCoveringPermissionItemId = 9614;

    public static IReadOnlyList<ChocoboInventoryForm> Enumerate(InventoryService inventoryService)
        => inventoryService
            .EnumerateItemsInRange(ProofOfCoveringItemId, LastCoveringPermissionItemId)
            .Select(item => ChocoboInventoryForm.TryDecode(item, out var form) ? form : (ChocoboInventoryForm?)null)
            .Where(form => form.HasValue)
            .Select(form => form!.Value)
            .OrderBy(form => (int)form.Container)
            .ThenBy(form => form.Slot)
            .ToArray();
}

public readonly record struct ActiveRacerSnapshot(
    bool IsLoaded,
    int Rank,
    int Pedigree,
    ChocoboSex Sex,
    bool NeedsFeeding);

public enum TargetPlanAction
{
    Blocked,
    Race,
    FeedActive,
    RetireActive,
    RegisterFledgling,
    CoverPair,
    WaitForCovering,
    AdoptFledgling,
    TargetReady,
}

public enum CoveringPurpose
{
    None,
    AdvancePedigree,
    ProduceMissingSex,
}

public sealed record TargetPedigreePlan(
    TargetPlanAction Action,
    string Reason,
    ChocoboInventoryForm? Primary,
    ChocoboInventoryForm? Partner,
    CoveringPurpose CoveringPurpose)
{
    public static TargetPedigreePlan Blocked(string reason)
        => new(TargetPlanAction.Blocked, reason, null, null, CoveringPurpose.None);
}

public static class TargetPedigreePlanner
{
    public static TargetPedigreePlan Plan(
        int targetPedigree,
        int retirementRank,
        ActiveRacerSnapshot racer,
        IReadOnlyList<ChocoboInventoryForm> forms)
    {
        if (targetPedigree is < 2 or > 9)
            return TargetPedigreePlan.Blocked($"Target pedigree G{targetPedigree} is outside G2-G9.");
        if (retirementRank is < 40 or > 50)
            return TargetPedigreePlan.Blocked($"Retirement rank {retirementRank} is outside 40-50.");

        var usable = forms.Where(form => form.HasPositiveCapacity && form.Quantity > 0).ToArray();
        var ownedParents = usable
            .Where(form => form.Kind is ChocoboFormKind.Retired or ChocoboFormKind.CoveringPermission)
            .Where(form => form.Pedigree < targetPedigree)
            .ToArray();
        var targetFledgling = SelectFledgling(usable, targetPedigree);
        if (targetFledgling != null)
        {
            return new TargetPedigreePlan(
                TargetPlanAction.RegisterFledgling,
                $"Register the exact G{targetPedigree} fledgling in {targetFledgling.Value.Container} slot {targetFledgling.Value.Slot}.",
                targetFledgling,
                null,
                CoveringPurpose.None);
        }

        if (racer.IsLoaded)
        {
            if (racer.Sex == ChocoboSex.Unknown)
                return TargetPedigreePlan.Blocked("The active racer's sex is unavailable.");
            if (racer.Pedigree > targetPedigree)
                return TargetPedigreePlan.Blocked($"The active G{racer.Pedigree} racer is above target G{targetPedigree}.");

            if (racer.Pedigree == targetPedigree)
            {
                return racer.NeedsFeeding
                    ? new TargetPedigreePlan(TargetPlanAction.FeedActive, "Feed the target racer before declaring it ready.", null, null, CoveringPurpose.None)
                    : new TargetPedigreePlan(TargetPlanAction.TargetReady, $"The active G{targetPedigree} racer is target-ready.", null, null, CoveringPurpose.None);
            }

            var highestOwnedPedigree = ownedParents.Select(form => form.Pedigree).DefaultIfEmpty(0).Max();
            if (TrySelectPair(ownedParents, racer.Pedigree, out var currentPrimary, out var currentPartner))
            {
                return new TargetPedigreePlan(
                    TargetPlanAction.CoverPair,
                    $"Cover the exact G{racer.Pedigree} pair to advance toward G{targetPedigree}.",
                    currentPrimary,
                    currentPartner,
                    CoveringPurpose.AdvancePedigree);
            }

            var oppositeSexOwned = ownedParents.Any(form =>
                form.Pedigree == racer.Pedigree &&
                form.Sex != racer.Sex &&
                form.Sex != ChocoboSex.Unknown);
            if (!oppositeSexOwned &&
                racer.Pedigree > 1 &&
                TrySelectPair(ownedParents, racer.Pedigree - 1, out var precedingPrimary, out var precedingPartner))
            {
                return new TargetPedigreePlan(
                    TargetPlanAction.CoverPair,
                    $"Cover the exact G{racer.Pedigree - 1} pair to produce the missing G{racer.Pedigree} sex while the useful racer continues racing.",
                    precedingPrimary,
                    precedingPartner,
                    CoveringPurpose.ProduceMissingSex);
            }

            if (highestOwnedPedigree > racer.Pedigree)
            {
                var higherParentPlan = PlanOwnedParents(ownedParents, targetPedigree);
                if (higherParentPlan != null)
                    return higherParentPlan;
            }

            var activeSexAlreadyOwned = ownedParents.Any(form =>
                form.Kind == ChocoboFormKind.Retired &&
                form.Pedigree == racer.Pedigree &&
                form.Sex == racer.Sex);
            var activeIsUseful = racer.Pedigree >= highestOwnedPedigree && !activeSexAlreadyOwned;
            if (!activeIsUseful)
            {
                var parentPlan = PlanOwnedParents(ownedParents, targetPedigree);
                return parentPlan ?? TargetPedigreePlan.Blocked(
                    $"The active G{racer.Pedigree} {racer.Sex.ToString().ToLowerInvariant()} duplicates an owned usable parent and no productive pair is available.");
            }

            if (racer.Rank < retirementRank)
            {
                return new TargetPedigreePlan(
                    TargetPlanAction.Race,
                    $"Race the useful G{racer.Pedigree} {racer.Sex.ToString().ToLowerInvariant()} candidate to rank {retirementRank}.",
                    null,
                    null,
                    CoveringPurpose.None);
            }

            return racer.NeedsFeeding
                ? new TargetPedigreePlan(TargetPlanAction.FeedActive, "Use the active racer's remaining training sessions before retirement.", null, null, CoveringPurpose.None)
                : new TargetPedigreePlan(TargetPlanAction.RetireActive, $"Retire the active G{racer.Pedigree} {racer.Sex.ToString().ToLowerInvariant()} racer exactly.", null, null, CoveringPurpose.None);
        }

        if (usable.Any(form => form.Kind == ChocoboFormKind.Proof))
            return new TargetPedigreePlan(TargetPlanAction.WaitForCovering, "A Proof of Covering is present.", null, null, CoveringPurpose.None);

        var usefulFledgling = SelectUsefulFledgling(usable, targetPedigree);
        if (usefulFledgling != null)
        {
            return new TargetPedigreePlan(
                TargetPlanAction.RegisterFledgling,
                $"Register the exact useful G{usefulFledgling.Value.Pedigree} {usefulFledgling.Value.Sex.ToString().ToLowerInvariant()} fledgling.",
                usefulFledgling,
                null,
                CoveringPurpose.None);
        }

        var ownedPlan = PlanOwnedParents(ownedParents, targetPedigree);
        if (ownedPlan != null)
            return ownedPlan;

        var currentPedigree = ownedParents.Select(form => form.Pedigree).DefaultIfEmpty(0).Max();
        if (currentPedigree == 0)
            return TargetPedigreePlan.Blocked("No useful active racer, fledgling, or covering parents are available.");

        return TargetPedigreePlan.Blocked($"G{currentPedigree} is missing a usable opposite-sex parent and no preceding-pedigree pair can produce it.");
    }

    private static TargetPedigreePlan? PlanOwnedParents(
        IReadOnlyList<ChocoboInventoryForm> ownedParents,
        int targetPedigree)
    {
        var currentPedigree = ownedParents.Select(form => form.Pedigree).DefaultIfEmpty(0).Max();
        if (currentPedigree == 0)
            return null;

        if (TrySelectPair(ownedParents, currentPedigree, out var primary, out var partner))
        {
            return new TargetPedigreePlan(
                TargetPlanAction.CoverPair,
                $"Cover the exact G{currentPedigree} pair to advance toward G{targetPedigree}.",
                primary,
                partner,
                CoveringPurpose.AdvancePedigree);
        }

        if (currentPedigree > 1 && TrySelectPair(ownedParents, currentPedigree - 1, out primary, out partner))
        {
            return new TargetPedigreePlan(
                TargetPlanAction.CoverPair,
                $"Cover the exact G{currentPedigree - 1} pair to produce the missing G{currentPedigree} sex.",
                primary,
                partner,
                CoveringPurpose.ProduceMissingSex);
        }

        return null;
    }

    private static ChocoboInventoryForm? SelectFledgling(IEnumerable<ChocoboInventoryForm> forms, int pedigree)
        => OrderForms(forms.Where(form => form.Kind == ChocoboFormKind.Fledgling && form.Pedigree == pedigree)).FirstOrDefaultNullable();

    private static ChocoboInventoryForm? SelectUsefulFledgling(
        IReadOnlyList<ChocoboInventoryForm> forms,
        int targetPedigree)
    {
        var fledglings = forms
            .Where(form => form.Kind == ChocoboFormKind.Fledgling && form.Pedigree < targetPedigree)
            .OrderByDescending(form => form.Pedigree)
            .ThenBy(form => HasUsableRetiredSex(forms, form.Pedigree, form.Sex) ? 1 : 0)
            .ThenBy(form => form.Sex)
            .ThenBy(form => (int)form.Container)
            .ThenBy(form => form.Slot);
        return fledglings.FirstOrDefaultNullable();
    }

    private static bool TrySelectPair(
        IReadOnlyList<ChocoboInventoryForm> forms,
        int pedigree,
        out ChocoboInventoryForm? primary,
        out ChocoboInventoryForm? partner)
    {
        var males = OrderParents(forms.Where(form => form.Pedigree == pedigree && form.Sex == ChocoboSex.Male)).ToArray();
        var females = OrderParents(forms.Where(form => form.Pedigree == pedigree && form.Sex == ChocoboSex.Female)).ToArray();
        foreach (var male in males)
        {
            foreach (var female in females)
            {
                if (male.Kind != ChocoboFormKind.Retired && female.Kind != ChocoboFormKind.Retired)
                    continue;

                primary = male.Kind == ChocoboFormKind.Retired ? male : female;
                partner = male.Kind == ChocoboFormKind.Retired ? female : male;
                return true;
            }
        }

        primary = null;
        partner = null;
        return false;
    }

    private static bool HasUsableRetiredSex(
        IEnumerable<ChocoboInventoryForm> forms,
        int pedigree,
        ChocoboSex sex)
        => forms.Any(form =>
            form.Kind == ChocoboFormKind.Retired &&
            form.Pedigree == pedigree &&
            form.Sex == sex &&
            form.Capacity > 0);

    private static IOrderedEnumerable<ChocoboInventoryForm> OrderParents(IEnumerable<ChocoboInventoryForm> forms)
        => forms
            .OrderBy(form => form.Kind == ChocoboFormKind.Retired ? 0 : 1)
            .ThenByDescending(form => form.Capacity > 0)
            .ThenByDescending(form => form.Capacity)
            .ThenBy(form => (int)form.Container)
            .ThenBy(form => form.Slot);

    private static IOrderedEnumerable<ChocoboInventoryForm> OrderForms(IEnumerable<ChocoboInventoryForm> forms)
        => forms
            .OrderByDescending(form => form.Capacity > 0)
            .ThenByDescending(form => form.Capacity)
            .ThenBy(form => form.Sex)
            .ThenBy(form => (int)form.Container)
            .ThenBy(form => form.Slot);

    private static ChocoboInventoryForm? FirstOrDefaultNullable(this IEnumerable<ChocoboInventoryForm> forms)
    {
        foreach (var form in forms)
            return form;
        return null;
    }
}
