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

public enum OffspringGoal { ReachPedigree, AbilityOffspring, ColourOffspring }

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

    public static unsafe bool TryReadColour(ChocoboInventoryForm form, out uint colourId)
    {
        colourId = 0;
        if (form.Kind is not (ChocoboFormKind.Fledgling or ChocoboFormKind.Retired)) return false;
        var manager = InventoryManager.Instance();
        var container = manager == null ? null : manager->GetInventoryContainer(form.Container);
        if (container == null || form.Slot < 0 || form.Slot >= container->Size) return false;
        var item = container->GetInventorySlot(form.Slot);
        if (item == null || item->ItemId != form.ItemId || item->Quantity != form.Quantity || item->Condition != form.Capacity)
            return false;
        // Both observed colours agreed with seven exact native form tooltips on 2026-09-30.
        var nativeColourId = item->Stains[0];
        if (nativeColourId == 0 ||
            !Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Stain>().TryGetRow(nativeColourId, out var colour) ||
            string.IsNullOrWhiteSpace(colour.Name.ExtractText()))
            return false;
        colourId = nativeColourId;
        return true;
    }

    public static unsafe bool TryReadInheritedAbility(ChocoboInventoryForm form, out uint abilityId)
    {
        abilityId = 0;
        if (form.Kind is not (ChocoboFormKind.Fledgling or ChocoboFormKind.Retired)) return false;
        var manager = InventoryManager.Instance();
        var container = manager == null ? null : manager->GetInventoryContainer(form.Container);
        if (container == null || form.Slot < 0 || form.Slot >= container->Size) return false;
        var item = container->GetInventorySlot(form.Slot);
        if (item == null || item->ItemId != form.ItemId || item->Quantity != form.Quantity || item->Condition != form.Capacity)
            return false;
        // Seven exact tooltips and a fledgling -> native registered-racer comparison agreed on this encoding.
        var nativeAbilityId = ((uint)item->Materia[4] << 4) | item->MateriaGrades[4];
        if (nativeAbilityId == 0 ||
            !Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.ChocoboRaceAbility>().TryGetRow(nativeAbilityId, out var ability) ||
            string.IsNullOrWhiteSpace(ability.Name.ExtractText())) return false;
        abilityId = nativeAbilityId;
        return true;
    }
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
    BuyRegistrationForm,
    BuyCoveringPermit,
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
    CoveringPurpose CoveringPurpose,
    uint RequiredItemId = 0)
{
    public static TargetPedigreePlan Blocked(string reason)
        => new(TargetPlanAction.Blocked, reason, null, null, CoveringPurpose.None);
}

public static class TargetPedigreePlanner
{
    public static TargetPedigreePlan PlanOffspring(ActiveRacerSnapshot racer,
        IReadOnlyList<ChocoboInventoryForm> forms, BreedingMode mode, int produced, int requested,
        Func<ChocoboInventoryForm, bool?> matches, bool? racerMatches)
    {
        if (!Enum.IsDefined(mode) || requested < 1 || produced < 0)
            return TargetPedigreePlan.Blocked("Invalid offspring production settings.");
        if (produced >= requested)
            return new(TargetPlanAction.TargetReady, $"Produced {produced}/{requested} matching G9 offspring; retained unregistered.",
                null, null, CoveringPurpose.None);
        if (forms.Any(form => form.Kind == ChocoboFormKind.Proof && form.Quantity > 0))
            return new(TargetPlanAction.WaitForCovering, "Reconcile the existing covering before producing another offspring.",
                null, null, CoveringPurpose.None);
        if (racer.IsLoaded)
        {
            if (racer.Pedigree != 9 || racer.Sex == ChocoboSex.Unknown || racer.Rank is < 1 or > 50)
                return TargetPedigreePlan.Blocked("Offspring production requires G9 breeding stock; lower-generation racing is stopped.");
            if (racerMatches != true && !(racerMatches == false && forms.Any(form => form.Kind == ChocoboFormKind.Retired &&
                form.Pedigree == 9 && form.HasPositiveCapacity && form.Sex != racer.Sex && matches(form) == true)))
                return TargetPedigreePlan.Blocked("The registered G9 racer does not have a confirmed requested trait. Retain it and select suitable breeding stock.");
            return new(racer.Rank >= 40 ? TargetPlanAction.RetireActive : racer.NeedsFeeding ? TargetPlanAction.FeedActive : TargetPlanAction.Race,
                $"Raise the matching G9 parent to retirement: racing rank {racer.Rank}/40. Matching offspring stay unregistered.",
                null, null, CoveringPurpose.None);
        }
        var parents = OrderParents(forms.Where(form => form.Kind == ChocoboFormKind.Retired &&
            form.Pedigree == 9 && form.Quantity > 0 && form.HasPositiveCapacity)).ToArray();
        foreach (var parent in parents.Where(parent => matches(parent) == true))
        {
            if (mode == BreedingMode.NpcPermits)
                return PlanPermit(parent, forms, CoveringPurpose.AdvancePedigree);
            var partner = parents.FirstOrDefaultNullable(other => other.Sex != parent.Sex && other.Sex != ChocoboSex.Unknown);
            if (partner.HasValue)
                return new(TargetPlanAction.CoverPair, "Cover matching G9 stock with its retained opposite-sex G9 parent.",
                    parent, partner, CoveringPurpose.AdvancePedigree);
        }
        if (!parents.Any(parent => matches(parent) == true))
            return TargetPedigreePlan.Blocked("No usable retired G9 parent has a confirmed requested trait. Retain offspring and replenish suitable G9 stock.");
        // Only non-matching G9 offspring may replace an exhausted/missing counterpart.
        // Unknown traits and matching outputs are never consumed as breeding stock.
        var replacement = forms.FirstOrDefaultNullable(form => form.Kind == ChocoboFormKind.Fledgling &&
            form.Pedigree == 9 && form.Quantity > 0 && matches(form) == false &&
            parents.Any(parent => matches(parent) == true && parent.Sex != form.Sex));
        if (replacement.HasValue)
            return new(TargetPlanAction.RegisterFledgling, "Raise this non-matching G9 offspring as the missing owned counterpart; retain matching outputs.",
                replacement, null, CoveringPurpose.ProduceMissingSex);
        return TargetPedigreePlan.Blocked("Missing a usable opposite-sex G9 parent. Owned mode requires suitable G9 stock or an explicit switch to NPC permits.");
    }

    public static TargetPedigreePlan PlanProgression(int targetPedigree, ActiveRacerSnapshot racer,
        IReadOnlyList<ChocoboInventoryForm> forms, BreedingMode mode, bool produceCounterpart = false)
    {
        if (targetPedigree is < 2 or > 9 || !Enum.IsDefined(mode))
            return TargetPedigreePlan.Blocked("Invalid progression settings.");
        // Exhausted parents still prove the pedigree already reached. Never rebuild below it.
        var pedigreeFloor = Math.Max(racer.IsLoaded ? racer.Pedigree : 0,
            forms.Where(form => form.Quantity > 0 && form.Kind is ChocoboFormKind.Retired or ChocoboFormKind.Fledgling)
                .Select(form => form.Pedigree).DefaultIfEmpty(0).Max());
        if (targetPedigree < pedigreeFloor)
            return TargetPedigreePlan.Blocked($"G{pedigreeFloor} has already been reached; the target cannot move backwards.");
        if (produceCounterpart && (mode != BreedingMode.NpcPermits || pedigreeFloor < 2))
            return TargetPedigreePlan.Blocked("Counterpart covering requires permit mode and a reached pedigree of G2 or higher.");
        var usable = forms.Where(form => form.Quantity > 0 && form.HasPositiveCapacity).ToArray();
        if (racer.IsLoaded)
        {
            if (racer.Pedigree is < 1 or > 9 || racer.Rank is < 1 or > 50 || racer.Sex == ChocoboSex.Unknown)
                return TargetPedigreePlan.Blocked("Current racer pedigree, racing rank, or sex is unavailable.");
            if (racer.Pedigree < pedigreeFloor)
                return TargetPedigreePlan.Blocked($"The registered G{racer.Pedigree} racer is below the retained G{pedigreeFloor} pedigree; lower-generation racing is stopped.");
            if (racer.Pedigree >= targetPedigree)
                return racer.Pedigree == targetPedigree
                    ? new(racer.Rank == 50 ? TargetPlanAction.TargetReady : racer.NeedsFeeding ? TargetPlanAction.FeedActive : TargetPlanAction.Race,
                        $"Retain G{racer.Pedigree}; racing rank {racer.Rank}/50.", null, null, CoveringPurpose.None)
                    : TargetPedigreePlan.Blocked("The current racer exceeds the selected target pedigree; retain it.");
            return new(racer.Rank >= 40 ? TargetPlanAction.RetireActive : racer.NeedsFeeding ? TargetPlanAction.FeedActive : TargetPlanAction.Race,
                $"G{racer.Pedigree} {racer.Sex}: racing rank {racer.Rank}/40 before retirement.", null, null, CoveringPurpose.None);
        }
        var proof = usable.FirstOrDefaultNullable(form => form.Kind == ChocoboFormKind.Proof);
        if (proof.HasValue)
            return new(TargetPlanAction.WaitForCovering, "Reconcile the existing Proof of Covering before another covering.", proof, null, CoveringPurpose.None);
        var currentForms = usable.Where(form => form.Kind != ChocoboFormKind.Fledgling || form.Pedigree >= pedigreeFloor).ToArray();
        var usefulCandidates = currentForms.Where(form => form.Kind != ChocoboFormKind.Fledgling ||
            !HasUsableRetiredSex(currentForms, form.Pedigree, form.Sex)).ToArray();
        var fledgling = SelectFledgling(produceCounterpart ? usefulCandidates : currentForms, targetPedigree)
            ?? SelectUsefulFledgling(usefulCandidates, targetPedigree);
        if (fledgling.HasValue)
            return new(TargetPlanAction.RegisterFledgling, $"Register G{fledgling.Value.Pedigree} {fledgling.Value.Sex}.", fledgling, null, CoveringPurpose.None);
        var parents = usable.Where(form => form.Kind == ChocoboFormKind.Retired && form.Pedigree < targetPedigree).ToArray();
        var highest = parents.Select(form => form.Pedigree).DefaultIfEmpty(0).Max();
        if (produceCounterpart)
        {
            if (TrySelectPair(usable.Where(form => form.Kind == ChocoboFormKind.Retired).ToArray(), pedigreeFloor, out _, out _))
                return TargetPedigreePlan.Blocked($"A usable G{pedigreeFloor} pair is already retained. Select owned parents or advance-pedigree permits before another covering.");
            var preceding = pedigreeFloor - 1;
            var source = OrderParents(parents.Where(form => form.Pedigree == preceding)).FirstOrDefaultNullable();
            if (!source.HasValue)
                return TargetPedigreePlan.Blocked($"No usable G{preceding} parent remains to produce a G{pedigreeFloor} counterpart. Lower-generation covering is stopped.");
            return PlanPermit(source.Value, usable, CoveringPurpose.ProduceMissingSex);
        }
        if (mode == BreedingMode.NpcPermits && highest > 0)
        {
            if (highest + 1 < pedigreeFloor)
                return TargetPedigreePlan.Blocked($"No retained parent can produce G{pedigreeFloor} or higher; lower-generation covering is stopped.");
            var parent = OrderParents(parents.Where(form => form.Pedigree == highest)).First();
            return PlanPermit(parent, usable, CoveringPurpose.AdvancePedigree);
        }
        // A preceding pair may produce another current-grade parent, but never a lower grade.
        // Owned mode does not purchase a permit without an explicit mode change.
        for (var pedigree = highest; pedigree >= Math.Max(1, pedigreeFloor - 1); --pedigree)
            if (TrySelectPair(parents, pedigree, out var primary, out var partner))
                return new(TargetPlanAction.CoverPair, $"Cover retained G{pedigree} parents; preserve their remaining capacity.",
                    primary, partner, pedigree == highest ? CoveringPurpose.AdvancePedigree : CoveringPurpose.ProduceMissingSex);
        if (pedigreeFloor > 1)
            return TargetPedigreePlan.Blocked($"Missing an owned G{pedigreeFloor} counterpart. No owned pair can produce G{pedigreeFloor} or higher; select a permitted covering explicitly. Lower-generation rebuilding is stopped.");
        var needFemale = parents.Any(form => form.Pedigree == 1 && form.Sex == ChocoboSex.Male);
        var registrationId = needFemale ? ChocoboInventoryModel.FirstFemaleFledglingItemId : ChocoboInventoryModel.FirstFledglingItemId;
        return new(TargetPlanAction.BuyRegistrationForm, $"Buy a G1 {(needFemale ? "female" : "male")} registration form to raise a missing owned parent.",
            null, null, CoveringPurpose.ProduceMissingSex, registrationId);
    }

    private static TargetPedigreePlan PlanPermit(ChocoboInventoryForm parent,
        IReadOnlyList<ChocoboInventoryForm> forms, CoveringPurpose purpose)
    {
        var permitId = (parent.Sex == ChocoboSex.Male ? ChocoboInventoryModel.FirstFemaleCoveringPermissionItemId
            : ChocoboInventoryModel.FirstCoveringPermissionItemId) + (uint)parent.Pedigree - 1;
        var permit = forms.FirstOrDefaultNullable(form => form.Kind == ChocoboFormKind.CoveringPermission && form.ItemId == permitId);
        var resultPedigree = Math.Min(9, parent.Pedigree + 1);
        return new(permit.HasValue ? TargetPlanAction.CoverPair : TargetPlanAction.BuyCoveringPermit,
            permit.HasValue ? $"Cover the retained G{parent.Pedigree} parent and matching opposite-sex permit to produce G{resultPedigree}."
                : $"Buy the opposite-sex G{parent.Pedigree} permit within reserves to produce G{resultPedigree}.",
            parent, permit, purpose, permitId);
    }

    private static ChocoboInventoryForm? FirstOrDefaultNullable(this IEnumerable<ChocoboInventoryForm> forms,
        Func<ChocoboInventoryForm, bool> predicate) => forms.Where(predicate).FirstOrDefaultNullable();

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
