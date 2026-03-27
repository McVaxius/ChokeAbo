namespace ChokeAbo.Services;

public sealed record FeedDefinition(
    ChocoboStatKind StatKind,
    string StatLabel,
    int Grade,
    string FeedName,
    FeedCurrencyKind CurrencyKind,
    uint UnitCost,
    string VendorCategoryLabel,
    int VendorCategoryIndex,
    string ExpectedAddonName,
    int VendorCallbackGroup,
    int VendorCallbackIndex);

public static class FeedCatalog
{
    private static readonly IReadOnlyList<FeedDefinition> Definitions = new[]
    {
        new FeedDefinition(ChocoboStatKind.Acceleration, "Acceleration", 1, "Grade 1 Feed - Acceleration Blend", FeedCurrencyKind.Gil, 1500, "Purchase Items (Gil)", 0, "Shop", 6, 2),
        new FeedDefinition(ChocoboStatKind.Cunning, "Cunning", 1, "Grade 1 Feed - Balance Blend", FeedCurrencyKind.Gil, 1500, "Purchase Items (Gil)", 0, "Shop", 6, 5),
        new FeedDefinition(ChocoboStatKind.Endurance, "Endurance", 1, "Grade 1 Feed - Endurance Blend", FeedCurrencyKind.Gil, 1500, "Purchase Items (Gil)", 0, "Shop", 6, 3),
        new FeedDefinition(ChocoboStatKind.MaximumSpeed, "Maximum Speed", 1, "Grade 1 Feed - Speed Blend", FeedCurrencyKind.Gil, 1500, "Purchase Items (Gil)", 0, "Shop", 6, 1),
        new FeedDefinition(ChocoboStatKind.Stamina, "Stamina", 1, "Grade 1 Feed - Stamina Blend", FeedCurrencyKind.Gil, 1500, "Purchase Items (Gil)", 0, "Shop", 6, 4),

        new FeedDefinition(ChocoboStatKind.Acceleration, "Acceleration", 2, "Grade 2 Feed - Special Acceleration Blend", FeedCurrencyKind.Mgp, 610, "Race Items", 1, "ShopExchangeCurrency", 0, 1),
        new FeedDefinition(ChocoboStatKind.Cunning, "Cunning", 2, "Grade 2 Feed - Special Balance Blend", FeedCurrencyKind.Mgp, 610, "Race Items", 1, "ShopExchangeCurrency", 0, 4),
        new FeedDefinition(ChocoboStatKind.Endurance, "Endurance", 2, "Grade 2 Feed - Special Endurance Blend", FeedCurrencyKind.Mgp, 610, "Race Items", 1, "ShopExchangeCurrency", 0, 2),
        new FeedDefinition(ChocoboStatKind.MaximumSpeed, "Maximum Speed", 2, "Grade 2 Feed - Special Speed Blend", FeedCurrencyKind.Mgp, 610, "Race Items", 1, "ShopExchangeCurrency", 0, 0),
        new FeedDefinition(ChocoboStatKind.Stamina, "Stamina", 2, "Grade 2 Feed - Special Stamina Blend", FeedCurrencyKind.Mgp, 610, "Race Items", 1, "ShopExchangeCurrency", 0, 3),

        new FeedDefinition(ChocoboStatKind.Acceleration, "Acceleration", 3, "Grade 3 Feed - Special Acceleration Blend", FeedCurrencyKind.Mgp, 1345, "Race Items", 1, "ShopExchangeCurrency", 0, 6),
        new FeedDefinition(ChocoboStatKind.Cunning, "Cunning", 3, "Grade 3 Feed - Special Balance Blend", FeedCurrencyKind.Mgp, 1345, "Race Items", 1, "ShopExchangeCurrency", 0, 9),
        new FeedDefinition(ChocoboStatKind.Endurance, "Endurance", 3, "Grade 3 Feed - Special Endurance Blend", FeedCurrencyKind.Mgp, 1345, "Race Items", 1, "ShopExchangeCurrency", 0, 7),
        new FeedDefinition(ChocoboStatKind.MaximumSpeed, "Maximum Speed", 3, "Grade 3 Feed - Special Speed Blend", FeedCurrencyKind.Mgp, 1345, "Race Items", 1, "ShopExchangeCurrency", 0, 5),
        new FeedDefinition(ChocoboStatKind.Stamina, "Stamina", 3, "Grade 3 Feed - Special Stamina Blend", FeedCurrencyKind.Mgp, 1345, "Race Items", 1, "ShopExchangeCurrency", 0, 8),
    };

    public static FeedDefinition Get(ChocoboStatKind statKind, int grade)
        => Definitions.Single(definition => definition.StatKind == statKind && definition.Grade == grade);
}
