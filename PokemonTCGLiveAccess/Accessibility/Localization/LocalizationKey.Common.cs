namespace Accessibility;

// Common.txt: shared navigation, readers (cards, shop, rank) and global shortcuts.
public sealed partial class LocalizationKey
{
    #region Common
    public static readonly LocalizationKey COMMON_STATE_FORMAT = new(nameof(COMMON_STATE_FORMAT));
    public static readonly LocalizationKey COMMON_NAME_COPIED = new(nameof(COMMON_NAME_COPIED));
    #endregion

    #region KeyboardNavigator
    public static readonly LocalizationKey NAV_ECHO_INSERTED = new(nameof(NAV_ECHO_INSERTED));
    public static readonly LocalizationKey NAV_ECHO_DELETED = new(nameof(NAV_ECHO_DELETED));
    public static readonly LocalizationKey NAV_ECHO_DELETED_FORMAT = new(nameof(NAV_ECHO_DELETED_FORMAT));
    public static readonly LocalizationKey NAV_ECHO_SPACE = new(nameof(NAV_ECHO_SPACE));
    public static readonly LocalizationKey NAV_CARET_START = new(nameof(NAV_CARET_START));
    public static readonly LocalizationKey NAV_CARET_END = new(nameof(NAV_CARET_END));
    public static readonly LocalizationKey NAV_FIELD_BLANK = new(nameof(NAV_FIELD_BLANK));
    public static readonly LocalizationKey NAV_EDIT_FIELD_FORMAT = new(nameof(NAV_EDIT_FIELD_FORMAT));
    #endregion

    #region UiLabels
    public static readonly LocalizationKey UI_BATTLE_PASS = new(nameof(UI_BATTLE_PASS));
    public static readonly LocalizationKey UI_QUESTS = new(nameof(UI_QUESTS));
    public static readonly LocalizationKey UI_MENU = new(nameof(UI_MENU));
    public static readonly LocalizationKey UI_RANK = new(nameof(UI_RANK));
    public static readonly LocalizationKey UI_CONFIRM_PURCHASE = new(nameof(UI_CONFIRM_PURCHASE));
    public static readonly LocalizationKey UI_RENAME_DECK = new(nameof(UI_RENAME_DECK));
    public static readonly LocalizationKey UI_DECK_OPTIONS = new(nameof(UI_DECK_OPTIONS));
    public static readonly LocalizationKey UI_EXPANSION = new(nameof(UI_EXPANSION));
    public static readonly LocalizationKey UI_EXPANSION_COLLECTED_FORMAT = new(nameof(UI_EXPANSION_COLLECTED_FORMAT));
    public static readonly LocalizationKey UI_INFORMATION_FORMAT = new(nameof(UI_INFORMATION_FORMAT));
    public static readonly LocalizationKey UI_CHOOSE_EXPANSION = new(nameof(UI_CHOOSE_EXPANSION));
    public static readonly LocalizationKey UI_CHANGE_FORMAT = new(nameof(UI_CHANGE_FORMAT));
    public static readonly LocalizationKey UI_CHANGE_FORMAT_CURRENT = new(nameof(UI_CHANGE_FORMAT_CURRENT));
    #endregion

    #region ShopLabels
    public static readonly LocalizationKey SHOP_PRODUCT = new(nameof(SHOP_PRODUCT));
    public static readonly LocalizationKey SHOP_PRODUCTS = new(nameof(SHOP_PRODUCTS));
    public static readonly LocalizationKey SHOP_SOLD_OUT = new(nameof(SHOP_SOLD_OUT));
    public static readonly LocalizationKey SHOP_AVAILABLE_FORMAT = new(nameof(SHOP_AVAILABLE_FORMAT));
    public static readonly LocalizationKey SHOP_CRYSTALS = new(nameof(SHOP_CRYSTALS));
    public static readonly LocalizationKey SHOP_CREDITS = new(nameof(SHOP_CREDITS));
    public static readonly LocalizationKey SHOP_TOKENS = new(nameof(SHOP_TOKENS));
    public static readonly LocalizationKey SHOP_KEYS = new(nameof(SHOP_KEYS));
    public static readonly LocalizationKey SHOP_COINS = new(nameof(SHOP_COINS));
    #endregion

    #region RankReader
    public static readonly LocalizationKey RANK_ONE_POINT = new(nameof(RANK_ONE_POINT));
    public static readonly LocalizationKey RANK_POINTS = new(nameof(RANK_POINTS));
    public static readonly LocalizationKey RANK_ONE_POINT_TO_NEXT = new(nameof(RANK_ONE_POINT_TO_NEXT));
    public static readonly LocalizationKey RANK_POINTS_TO_NEXT = new(nameof(RANK_POINTS_TO_NEXT));
    public static readonly LocalizationKey RANK_ELO = new(nameof(RANK_ELO));
    #endregion

    #region CardText / CardDexText
    public static readonly LocalizationKey CARD_CARD = new(nameof(CARD_CARD));
    public static readonly LocalizationKey CARD_BASIC_POKEMON = new(nameof(CARD_BASIC_POKEMON));
    public static readonly LocalizationKey CARD_STAGE1_POKEMON = new(nameof(CARD_STAGE1_POKEMON));
    public static readonly LocalizationKey CARD_STAGE2_POKEMON = new(nameof(CARD_STAGE2_POKEMON));
    public static readonly LocalizationKey CARD_POKEMON = new(nameof(CARD_POKEMON));
    public static readonly LocalizationKey CARD_TRAINER = new(nameof(CARD_TRAINER));
    public static readonly LocalizationKey CARD_ENERGY = new(nameof(CARD_ENERGY));
    public static readonly LocalizationKey CARD_VARIANT_MASTER_BALL = new(nameof(CARD_VARIANT_MASTER_BALL));
    public static readonly LocalizationKey CARD_VARIANT_POKE_BALL = new(nameof(CARD_VARIANT_POKE_BALL));
    public static readonly LocalizationKey CARD_VARIANT_REVERSE_HOLO = new(nameof(CARD_VARIANT_REVERSE_HOLO));
    public static readonly LocalizationKey CARD_VARIANT_ALTERNATE_ART = new(nameof(CARD_VARIANT_ALTERNATE_ART));
    public static readonly LocalizationKey CARD_HP = new(nameof(CARD_HP));
    public static readonly LocalizationKey CARD_NONE = new(nameof(CARD_NONE));
    public static readonly LocalizationKey CARD_WEAKNESS_VALUE = new(nameof(CARD_WEAKNESS_VALUE));
    public static readonly LocalizationKey CARD_RESISTANCE_VALUE = new(nameof(CARD_RESISTANCE_VALUE));
    public static readonly LocalizationKey CARD_STATS = new(nameof(CARD_STATS));
    public static readonly LocalizationKey CARD_DAMAGE = new(nameof(CARD_DAMAGE));
    public static readonly LocalizationKey CARD_COST = new(nameof(CARD_COST));
    public static readonly LocalizationKey CARD_ACE_SPEC_RULE = new(nameof(CARD_ACE_SPEC_RULE));
    public static readonly LocalizationKey CARD_KO_PRIZES_RULE = new(nameof(CARD_KO_PRIZES_RULE));
    public static readonly LocalizationKey CARD_MARKER_V_UNION = new(nameof(CARD_MARKER_V_UNION));
    public static readonly LocalizationKey CARD_OWNED = new(nameof(CARD_OWNED));
    public static readonly LocalizationKey CARD_NOT_OWNED = new(nameof(CARD_NOT_OWNED));
    public static readonly LocalizationKey CARD_NEW = new(nameof(CARD_NEW));
    public static readonly LocalizationKey CARD_NUMBER = new(nameof(CARD_NUMBER));
    #endregion
}
