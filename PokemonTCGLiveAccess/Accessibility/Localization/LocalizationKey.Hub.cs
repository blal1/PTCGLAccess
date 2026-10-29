namespace Accessibility;

// Hub.txt: HOME, Learning Lab, Build and Battle, matchmaking, League Tower, Battle Pass.
public sealed partial class LocalizationKey
{
    #region Shared
    public static readonly LocalizationKey HUB_HELP_WITH_GLOBAL_FORMAT = new(nameof(HUB_HELP_WITH_GLOBAL_FORMAT));
    public static readonly LocalizationKey HUB_GLOBAL_KEYS_HELP = new(nameof(HUB_GLOBAL_KEYS_HELP));
    public static readonly LocalizationKey HUB_EMPTY = new(nameof(HUB_EMPTY));
    public static readonly LocalizationKey HUB_EMPTY_FORMAT = new(nameof(HUB_EMPTY_FORMAT));
    public static readonly LocalizationKey HUB_ITEM = new(nameof(HUB_ITEM));
    public static readonly LocalizationKey HUB_INFORMATION = new(nameof(HUB_INFORMATION));
    public static readonly LocalizationKey HUB_BACK = new(nameof(HUB_BACK));
    public static readonly LocalizationKey HUB_PAGE_FORMAT = new(nameof(HUB_PAGE_FORMAT));
    public static readonly LocalizationKey HUB_FRACTION_FORMAT = new(nameof(HUB_FRACTION_FORMAT));
    public static readonly LocalizationKey HUB_LOCKED = new(nameof(HUB_LOCKED));
    #endregion

    #region Home
    public static readonly LocalizationKey HUB_HOME = new(nameof(HUB_HOME));
    public static readonly LocalizationKey HUB_HOME_HELP = new(nameof(HUB_HOME_HELP));
    public static readonly LocalizationKey HUB_GAME_MODE = new(nameof(HUB_GAME_MODE));
    public static readonly LocalizationKey HUB_NOT_SELECTED = new(nameof(HUB_NOT_SELECTED));
    public static readonly LocalizationKey HUB_CASUAL_INFO = new(nameof(HUB_CASUAL_INFO));
    #endregion

    #region Learning Lab
    public static readonly LocalizationKey HUB_LEARNING_LAB = new(nameof(HUB_LEARNING_LAB));
    public static readonly LocalizationKey HUB_LL_CLASSROOM_FORMAT = new(nameof(HUB_LL_CLASSROOM_FORMAT));
    public static readonly LocalizationKey HUB_LL_CLASSROOM_NO_DIFFICULTY_FORMAT = new(nameof(HUB_LL_CLASSROOM_NO_DIFFICULTY_FORMAT));
    public static readonly LocalizationKey HUB_LL_LESSON_COMPLETE = new(nameof(HUB_LL_LESSON_COMPLETE));
    public static readonly LocalizationKey HUB_LL_LESSON_NOT_COMPLETE = new(nameof(HUB_LL_LESSON_NOT_COMPLETE));
    public static readonly LocalizationKey HUB_DIFFICULTY_BEGINNER = new(nameof(HUB_DIFFICULTY_BEGINNER));
    public static readonly LocalizationKey HUB_DIFFICULTY_NOVICE = new(nameof(HUB_DIFFICULTY_NOVICE));
    public static readonly LocalizationKey HUB_DIFFICULTY_INTERMEDIATE = new(nameof(HUB_DIFFICULTY_INTERMEDIATE));
    public static readonly LocalizationKey HUB_DIFFICULTY_ADVANCED = new(nameof(HUB_DIFFICULTY_ADVANCED));
    public static readonly LocalizationKey HUB_DIFFICULTY_EXPERT = new(nameof(HUB_DIFFICULTY_EXPERT));
    public static readonly LocalizationKey HUB_DIFFICULTY_MASTER = new(nameof(HUB_DIFFICULTY_MASTER));
    #endregion

    #region Build and Battle
    public static readonly LocalizationKey HUB_BNB = new(nameof(HUB_BNB));
    public static readonly LocalizationKey HUB_BNB_INSTRUCTIONS = new(nameof(HUB_BNB_INSTRUCTIONS));
    public static readonly LocalizationKey HUB_PLAY = new(nameof(HUB_PLAY));
    public static readonly LocalizationKey HUB_BNB_LOBBY = new(nameof(HUB_BNB_LOBBY));
    public static readonly LocalizationKey HUB_BNB_MATCHES_FORMAT = new(nameof(HUB_BNB_MATCHES_FORMAT));
    public static readonly LocalizationKey HUB_BNB_LOBBY_HELP = new(nameof(HUB_BNB_LOBBY_HELP));
    public static readonly LocalizationKey HUB_POPUP_HELP = new(nameof(HUB_POPUP_HELP));
    #endregion

    #region Matchmaking
    public static readonly LocalizationKey HUB_SEARCHING = new(nameof(HUB_SEARCHING));
    public static readonly LocalizationKey HUB_OPPONENT_FOUND = new(nameof(HUB_OPPONENT_FOUND));
    public static readonly LocalizationKey HUB_MATCHMAKING_HELP = new(nameof(HUB_MATCHMAKING_HELP));
    #endregion

    #region League Tower
    public static readonly LocalizationKey HUB_LEAGUE_TOWER = new(nameof(HUB_LEAGUE_TOWER));
    public static readonly LocalizationKey HUB_LEAGUE_OPTIONS = new(nameof(HUB_LEAGUE_OPTIONS));
    public static readonly LocalizationKey HUB_SEASON_INFO = new(nameof(HUB_SEASON_INFO));
    public static readonly LocalizationKey HUB_CURRENT_RANK = new(nameof(HUB_CURRENT_RANK));
    public static readonly LocalizationKey HUB_ACHIEVED = new(nameof(HUB_ACHIEVED));
    public static readonly LocalizationKey HUB_LEAGUE_TOWER_HELP = new(nameof(HUB_LEAGUE_TOWER_HELP));
    #endregion

    #region Battle Pass (expansion challenge)
    public static readonly LocalizationKey HUB_BATTLE_PASS_HELP = new(nameof(HUB_BATTLE_PASS_HELP));
    public static readonly LocalizationKey HUB_DAILY_QUESTS = new(nameof(HUB_DAILY_QUESTS));
    public static readonly LocalizationKey HUB_REWARDS = new(nameof(HUB_REWARDS));
    public static readonly LocalizationKey HUB_QUEST = new(nameof(HUB_QUEST));
    public static readonly LocalizationKey HUB_ENDS_IN_FORMAT = new(nameof(HUB_ENDS_IN_FORMAT));
    public static readonly LocalizationKey HUB_LEVEL_FORMAT = new(nameof(HUB_LEVEL_FORMAT));
    public static readonly LocalizationKey HUB_EXPERIENCE_FORMAT = new(nameof(HUB_EXPERIENCE_FORMAT));
    public static readonly LocalizationKey HUB_REWARD_FORMAT = new(nameof(HUB_REWARD_FORMAT));
    public static readonly LocalizationKey HUB_AND_FORMAT = new(nameof(HUB_AND_FORMAT));
    public static readonly LocalizationKey HUB_TIMES_FORMAT = new(nameof(HUB_TIMES_FORMAT));
    public static readonly LocalizationKey HUB_AMOUNT_FORMAT = new(nameof(HUB_AMOUNT_FORMAT));
    public static readonly LocalizationKey HUB_CLAIMED = new(nameof(HUB_CLAIMED));
    public static readonly LocalizationKey HUB_CLAIMED_AVAILABLE_IN_FORMAT = new(nameof(HUB_CLAIMED_AVAILABLE_IN_FORMAT));
    public static readonly LocalizationKey HUB_READY_TO_COLLECT = new(nameof(HUB_READY_TO_COLLECT));
    public static readonly LocalizationKey HUB_CAN_BE_REPLACED = new(nameof(HUB_CAN_BE_REPLACED));
    public static readonly LocalizationKey HUB_COLLECT_ALL = new(nameof(HUB_COLLECT_ALL));
    public static readonly LocalizationKey HUB_TIER = new(nameof(HUB_TIER));
    public static readonly LocalizationKey HUB_TIER_FORMAT = new(nameof(HUB_TIER_FORMAT));
    public static readonly LocalizationKey HUB_ITEM_BOOSTER = new(nameof(HUB_ITEM_BOOSTER));
    public static readonly LocalizationKey HUB_ITEM_COIN = new(nameof(HUB_ITEM_COIN));
    public static readonly LocalizationKey HUB_ITEM_SLEEVE = new(nameof(HUB_ITEM_SLEEVE));
    public static readonly LocalizationKey HUB_ITEM_DECK_BOX = new(nameof(HUB_ITEM_DECK_BOX));
    public static readonly LocalizationKey HUB_ITEM_DECK = new(nameof(HUB_ITEM_DECK));
    public static readonly LocalizationKey HUB_ITEM_AVATAR = new(nameof(HUB_ITEM_AVATAR));
    #endregion
}
