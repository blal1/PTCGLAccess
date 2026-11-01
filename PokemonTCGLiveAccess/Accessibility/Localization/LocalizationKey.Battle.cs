namespace Accessibility;

// Battle.txt: the battle board, its choices, battle menu, match intro and result (Screens/Battle).
public sealed partial class LocalizationKey
{
    #region Board zones
    public static readonly LocalizationKey BATTLE_ZONE_MY_ACTIVE = new(nameof(BATTLE_ZONE_MY_ACTIVE));
    public static readonly LocalizationKey BATTLE_ZONE_MY_BENCH = new(nameof(BATTLE_ZONE_MY_BENCH));
    public static readonly LocalizationKey BATTLE_ZONE_MY_HAND = new(nameof(BATTLE_ZONE_MY_HAND));
    public static readonly LocalizationKey BATTLE_ZONE_MY_DISCARD = new(nameof(BATTLE_ZONE_MY_DISCARD));
    public static readonly LocalizationKey BATTLE_ZONE_MY_DECK = new(nameof(BATTLE_ZONE_MY_DECK));
    public static readonly LocalizationKey BATTLE_ZONE_MY_PRIZES = new(nameof(BATTLE_ZONE_MY_PRIZES));
    public static readonly LocalizationKey BATTLE_ZONE_MY_LOST_ZONE = new(nameof(BATTLE_ZONE_MY_LOST_ZONE));
    public static readonly LocalizationKey BATTLE_ZONE_OPP_ACTIVE = new(nameof(BATTLE_ZONE_OPP_ACTIVE));
    public static readonly LocalizationKey BATTLE_ZONE_OPP_BENCH = new(nameof(BATTLE_ZONE_OPP_BENCH));
    public static readonly LocalizationKey BATTLE_ZONE_OPP_HAND = new(nameof(BATTLE_ZONE_OPP_HAND));
    public static readonly LocalizationKey BATTLE_ZONE_OPP_DISCARD = new(nameof(BATTLE_ZONE_OPP_DISCARD));
    public static readonly LocalizationKey BATTLE_ZONE_OPP_DECK = new(nameof(BATTLE_ZONE_OPP_DECK));
    public static readonly LocalizationKey BATTLE_ZONE_OPP_PRIZES = new(nameof(BATTLE_ZONE_OPP_PRIZES));
    public static readonly LocalizationKey BATTLE_ZONE_STADIUM = new(nameof(BATTLE_ZONE_STADIUM));
    public static readonly LocalizationKey BATTLE_ZONE_AND_CARD = new(nameof(BATTLE_ZONE_AND_CARD));
    public static readonly LocalizationKey BATTLE_ZONE_ONE_CARD = new(nameof(BATTLE_ZONE_ONE_CARD));
    public static readonly LocalizationKey BATTLE_ZONE_CARDS = new(nameof(BATTLE_ZONE_CARDS));
    public static readonly LocalizationKey BATTLE_ZONE_EMPTY = new(nameof(BATTLE_ZONE_EMPTY));
    public static readonly LocalizationKey BATTLE_COPIES = new(nameof(BATTLE_COPIES));
    #endregion

    #region Card reading
    public static readonly LocalizationKey BATTLE_EMPTY = new(nameof(BATTLE_EMPTY));
    public static readonly LocalizationKey BATTLE_FACE_DOWN = new(nameof(BATTLE_FACE_DOWN));
    public static readonly LocalizationKey BATTLE_FACE_DOWN_NO_DETAILS = new(nameof(BATTLE_FACE_DOWN_NO_DETAILS));
    public static readonly LocalizationKey BATTLE_HP_SUMMARY = new(nameof(BATTLE_HP_SUMMARY));
    public static readonly LocalizationKey BATTLE_HP = new(nameof(BATTLE_HP));
    public static readonly LocalizationKey BATTLE_NO_ENERGY = new(nameof(BATTLE_NO_ENERGY));
    public static readonly LocalizationKey BATTLE_ENERGY_LIST = new(nameof(BATTLE_ENERGY_LIST));
    public static readonly LocalizationKey BATTLE_TOOL_LIST = new(nameof(BATTLE_TOOL_LIST));
    public static readonly LocalizationKey BATTLE_STATUS_LIST = new(nameof(BATTLE_STATUS_LIST));
    public static readonly LocalizationKey BATTLE_EVOLVED_FROM = new(nameof(BATTLE_EVOLVED_FROM));
    public static readonly LocalizationKey BATTLE_CARD_ITEM = new(nameof(BATTLE_CARD_ITEM));
    public static readonly LocalizationKey BATTLE_CARD_SUPPORTER = new(nameof(BATTLE_CARD_SUPPORTER));
    public static readonly LocalizationKey BATTLE_CARD_STADIUM = new(nameof(BATTLE_CARD_STADIUM));
    public static readonly LocalizationKey BATTLE_CARD_TOOL = new(nameof(BATTLE_CARD_TOOL));
    public static readonly LocalizationKey BATTLE_END_OF_CARD = new(nameof(BATTLE_END_OF_CARD));
    public static readonly LocalizationKey BATTLE_NOTHING_FOCUSED = new(nameof(BATTLE_NOTHING_FOCUSED));
    public static readonly LocalizationKey BATTLE_NO_CARDS_HERE = new(nameof(BATTLE_NO_CARDS_HERE));
    public static readonly LocalizationKey BATTLE_NO_CARD_THERE = new(nameof(BATTLE_NO_CARD_THERE));
    #endregion

    #region Battle flow
    public static readonly LocalizationKey BATTLE_NAME = new(nameof(BATTLE_NAME));
    public static readonly LocalizationKey BATTLE_STARTED = new(nameof(BATTLE_STARTED));
    public static readonly LocalizationKey BATTLE_STARTED_PHASE = new(nameof(BATTLE_STARTED_PHASE));
    public static readonly LocalizationKey BATTLE_ENDED = new(nameof(BATTLE_ENDED));
    public static readonly LocalizationKey BATTLE_PHASE_SETUP = new(nameof(BATTLE_PHASE_SETUP));
    public static readonly LocalizationKey BATTLE_PHASE_YOUR_TURN = new(nameof(BATTLE_PHASE_YOUR_TURN));
    public static readonly LocalizationKey BATTLE_PHASE_OPPONENT_TURN = new(nameof(BATTLE_PHASE_OPPONENT_TURN));
    public static readonly LocalizationKey BATTLE_COIN_HEADS = new(nameof(BATTLE_COIN_HEADS));
    public static readonly LocalizationKey BATTLE_COIN_TAILS = new(nameof(BATTLE_COIN_TAILS));
    public static readonly LocalizationKey BATTLE_DREW_CARD = new(nameof(BATTLE_DREW_CARD));
    public static readonly LocalizationKey BATTLE_DREW_NAMED = new(nameof(BATTLE_DREW_NAMED));
    public static readonly LocalizationKey BATTLE_SENTENCES = new(nameof(BATTLE_SENTENCES));
    public static readonly LocalizationKey BATTLE_STATUS_PRIZES = new(nameof(BATTLE_STATUS_PRIZES));
    public static readonly LocalizationKey BATTLE_STATUS_MY_ACTIVE = new(nameof(BATTLE_STATUS_MY_ACTIVE));
    public static readonly LocalizationKey BATTLE_STATUS_OPP_ACTIVE = new(nameof(BATTLE_STATUS_OPP_ACTIVE));
    public static readonly LocalizationKey BATTLE_NOT_YOUR_TURN = new(nameof(BATTLE_NOT_YOUR_TURN));
    public static readonly LocalizationKey BATTLE_FINISH_SELECTION_FIRST = new(nameof(BATTLE_FINISH_SELECTION_FIRST));
    public static readonly LocalizationKey BATTLE_CANT_END_TURN = new(nameof(BATTLE_CANT_END_TURN));
    public static readonly LocalizationKey BATTLE_DONE = new(nameof(BATTLE_DONE));
    public static readonly LocalizationKey BATTLE_DONE_UNAVAILABLE = new(nameof(BATTLE_DONE_UNAVAILABLE));
    public static readonly LocalizationKey BATTLE_CANT_CONFIRM = new(nameof(BATTLE_CANT_CONFIRM));
    #endregion

    #region Card view
    public static readonly LocalizationKey BATTLE_CARD_VIEW = new(nameof(BATTLE_CARD_VIEW));
    public static readonly LocalizationKey BATTLE_CARD_VIEW_CLOSED = new(nameof(BATTLE_CARD_VIEW_CLOSED));
    public static readonly LocalizationKey BATTLE_CANT_INSPECT = new(nameof(BATTLE_CANT_INSPECT));
    #endregion

    #region Actions
    public static readonly LocalizationKey BATTLE_NOTHING_TO_ACT_ON = new(nameof(BATTLE_NOTHING_TO_ACT_ON));
    public static readonly LocalizationKey BATTLE_NO_ACTIONS = new(nameof(BATTLE_NO_ACTIONS));
    public static readonly LocalizationKey BATTLE_ACTIONS_FOR = new(nameof(BATTLE_ACTIONS_FOR));
    public static readonly LocalizationKey BATTLE_ATTACK = new(nameof(BATTLE_ATTACK));
    public static readonly LocalizationKey BATTLE_USE_ABILITY = new(nameof(BATTLE_USE_ABILITY));
    public static readonly LocalizationKey BATTLE_ATTACK_NAMED = new(nameof(BATTLE_ATTACK_NAMED));
    public static readonly LocalizationKey BATTLE_ABILITY_NAMED = new(nameof(BATTLE_ABILITY_NAMED));
    public static readonly LocalizationKey BATTLE_RETREAT = new(nameof(BATTLE_RETREAT));
    public static readonly LocalizationKey BATTLE_FIRST_ACTION = new(nameof(BATTLE_FIRST_ACTION));
    public static readonly LocalizationKey BATTLE_LAST_ACTION = new(nameof(BATTLE_LAST_ACTION));
    public static readonly LocalizationKey BATTLE_NOT_USABLE_NOW = new(nameof(BATTLE_NOT_USABLE_NOW));
    public static readonly LocalizationKey BATTLE_TUTORIAL_FIRST = new(nameof(BATTLE_TUTORIAL_FIRST));
    public static readonly LocalizationKey BATTLE_TUTORIAL_NOT_READY = new(nameof(BATTLE_TUTORIAL_NOT_READY));
    public static readonly LocalizationKey BATTLE_TUTORIAL_BEFORE_END_TURN = new(nameof(BATTLE_TUTORIAL_BEFORE_END_TURN));
    public static readonly LocalizationKey BATTLE_TUTORIAL_WANTS = new(nameof(BATTLE_TUTORIAL_WANTS));
    public static readonly LocalizationKey BATTLE_TUTORIAL_WANTS_IN = new(nameof(BATTLE_TUTORIAL_WANTS_IN));
    #endregion

    #region Playing cards
    public static readonly LocalizationKey BATTLE_CANT_PLAY_THIS = new(nameof(BATTLE_CANT_PLAY_THIS));
    public static readonly LocalizationKey BATTLE_CANT_PLAY_RIGHT_NOW = new(nameof(BATTLE_CANT_PLAY_RIGHT_NOW));
    public static readonly LocalizationKey BATTLE_CANT_PLAY_NOW = new(nameof(BATTLE_CANT_PLAY_NOW));
    public static readonly LocalizationKey BATTLE_USE_CARD_QUESTION = new(nameof(BATTLE_USE_CARD_QUESTION));
    public static readonly LocalizationKey BATTLE_HOLDING = new(nameof(BATTLE_HOLDING));
    public static readonly LocalizationKey BATTLE_NOT_A_VALID_SPOT = new(nameof(BATTLE_NOT_A_VALID_SPOT));
    public static readonly LocalizationKey BATTLE_CANCELLED = new(nameof(BATTLE_CANCELLED));
    public static readonly LocalizationKey BATTLE_CANCELLED_CARD = new(nameof(BATTLE_CANCELLED_CARD));
    #endregion

    #region Attach and move energy
    public static readonly LocalizationKey BATTLE_NO_ENERGY_TO_ATTACH = new(nameof(BATTLE_NO_ENERGY_TO_ATTACH));
    public static readonly LocalizationKey BATTLE_ATTACH_ENERGY_ZONE = new(nameof(BATTLE_ATTACH_ENERGY_ZONE));
    public static readonly LocalizationKey BATTLE_SELECT_HAND_ENERGY = new(nameof(BATTLE_SELECT_HAND_ENERGY));
    public static readonly LocalizationKey BATTLE_CANT_ATTACH = new(nameof(BATTLE_CANT_ATTACH));
    public static readonly LocalizationKey BATTLE_NO_ENERGY_TO_MOVE_ON = new(nameof(BATTLE_NO_ENERGY_TO_MOVE_ON));
    public static readonly LocalizationKey BATTLE_CHOOSE_MOVE_SOURCE = new(nameof(BATTLE_CHOOSE_MOVE_SOURCE));
    public static readonly LocalizationKey BATTLE_CANT_MOVE_ENERGY_FROM = new(nameof(BATTLE_CANT_MOVE_ENERGY_FROM));
    public static readonly LocalizationKey BATTLE_NO_ENERGY_TO_MOVE_FROM = new(nameof(BATTLE_NO_ENERGY_TO_MOVE_FROM));
    public static readonly LocalizationKey BATTLE_FROM_SOURCE = new(nameof(BATTLE_FROM_SOURCE));
    public static readonly LocalizationKey BATTLE_CAN_RECEIVE_ENERGY = new(nameof(BATTLE_CAN_RECEIVE_ENERGY));
    public static readonly LocalizationKey BATTLE_HAS_ENERGY_TO_MOVE = new(nameof(BATTLE_HAS_ENERGY_TO_MOVE));
    #endregion

    #region Help
    public static readonly LocalizationKey BATTLE_HELP = new(nameof(BATTLE_HELP));
    public static readonly LocalizationKey BATTLE_HELP_HOLDING = new(nameof(BATTLE_HELP_HOLDING));
    public static readonly LocalizationKey BATTLE_HELP_ACTION_MENU = new(nameof(BATTLE_HELP_ACTION_MENU));
    public static readonly LocalizationKey BATTLE_HELP_CARD_VIEW = new(nameof(BATTLE_HELP_CARD_VIEW));
    public static readonly LocalizationKey BATTLE_HELP_CONFIRM_PLAY = new(nameof(BATTLE_HELP_CONFIRM_PLAY));
    #endregion

    #region Prompts
    public static readonly LocalizationKey BATTLE_PROMPT_HELP = new(nameof(BATTLE_PROMPT_HELP));
    public static readonly LocalizationKey BATTLE_PROMPT_NEXT_COUNTER = new(nameof(BATTLE_PROMPT_NEXT_COUNTER));
    public static readonly LocalizationKey BATTLE_PROMPT_NO_VALID_CARDS = new(nameof(BATTLE_PROMPT_NO_VALID_CARDS));
    public static readonly LocalizationKey BATTLE_PROMPT_CHOICE_REQUIRED = new(nameof(BATTLE_PROMPT_CHOICE_REQUIRED));
    public static readonly LocalizationKey BATTLE_PROMPT_UNSUPPORTED = new(nameof(BATTLE_PROMPT_UNSUPPORTED));
    public static readonly LocalizationKey BATTLE_PROMPT_PRIZE_CARD = new(nameof(BATTLE_PROMPT_PRIZE_CARD));
    public static readonly LocalizationKey BATTLE_PROMPT_TAKE_PRIZE = new(nameof(BATTLE_PROMPT_TAKE_PRIZE));
    public static readonly LocalizationKey BATTLE_PROMPT_TAKE_PRIZES = new(nameof(BATTLE_PROMPT_TAKE_PRIZES));
    public static readonly LocalizationKey BATTLE_PROMPT_PRIZE_FAILED = new(nameof(BATTLE_PROMPT_PRIZE_FAILED));
    public static readonly LocalizationKey BATTLE_PROMPT_PRIZE_SELECTED_MORE = new(nameof(BATTLE_PROMPT_PRIZE_SELECTED_MORE));
    public static readonly LocalizationKey BATTLE_PROMPT_PRIZE_SELECTED = new(nameof(BATTLE_PROMPT_PRIZE_SELECTED));
    public static readonly LocalizationKey BATTLE_PROMPT_PRIZE_DESELECTED = new(nameof(BATTLE_PROMPT_PRIZE_DESELECTED));
    public static readonly LocalizationKey BATTLE_PROMPT_PRIZE_MAX = new(nameof(BATTLE_PROMPT_PRIZE_MAX));
    public static readonly LocalizationKey BATTLE_PROMPT_CHOOSE_AT_LEAST = new(nameof(BATTLE_PROMPT_CHOOSE_AT_LEAST));
    public static readonly LocalizationKey BATTLE_PROMPT_TOO_MANY = new(nameof(BATTLE_PROMPT_TOO_MANY));
    public static readonly LocalizationKey BATTLE_PROMPT_CHOOSE_CARD = new(nameof(BATTLE_PROMPT_CHOOSE_CARD));
    public static readonly LocalizationKey BATTLE_PROMPT_CHOOSE_CARDS = new(nameof(BATTLE_PROMPT_CHOOSE_CARDS));
    public static readonly LocalizationKey BATTLE_PROMPT_CHOOSE_UP_TO_CARDS = new(nameof(BATTLE_PROMPT_CHOOSE_UP_TO_CARDS));
    public static readonly LocalizationKey BATTLE_PROMPT_SELECT_FAILED = new(nameof(BATTLE_PROMPT_SELECT_FAILED));
    public static readonly LocalizationKey BATTLE_PROMPT_CHANGE_FAILED = new(nameof(BATTLE_PROMPT_CHANGE_FAILED));
    public static readonly LocalizationKey BATTLE_PROMPT_SELECTED_COUNT = new(nameof(BATTLE_PROMPT_SELECTED_COUNT));
    public static readonly LocalizationKey BATTLE_PROMPT_DESELECTED_COUNT = new(nameof(BATTLE_PROMPT_DESELECTED_COUNT));
    public static readonly LocalizationKey BATTLE_PROMPT_ENERGY_SELECT_FAILED = new(nameof(BATTLE_PROMPT_ENERGY_SELECT_FAILED));
    public static readonly LocalizationKey BATTLE_PROMPT_NO_MORE = new(nameof(BATTLE_PROMPT_NO_MORE));
    public static readonly LocalizationKey BATTLE_PROMPT_CHOOSE_POKEMON = new(nameof(BATTLE_PROMPT_CHOOSE_POKEMON));
    public static readonly LocalizationKey BATTLE_PROMPT_CHOOSE_POKEMONS = new(nameof(BATTLE_PROMPT_CHOOSE_POKEMONS));
    public static readonly LocalizationKey BATTLE_PROMPT_POKEMON_FAILED = new(nameof(BATTLE_PROMPT_POKEMON_FAILED));
    public static readonly LocalizationKey BATTLE_PROMPT_SELECTED_MORE = new(nameof(BATTLE_PROMPT_SELECTED_MORE));
    public static readonly LocalizationKey BATTLE_PROMPT_CHOOSE_ATTACK = new(nameof(BATTLE_PROMPT_CHOOSE_ATTACK));
    public static readonly LocalizationKey BATTLE_PROMPT_CHOOSE_SWITCH = new(nameof(BATTLE_PROMPT_CHOOSE_SWITCH));
    public static readonly LocalizationKey BATTLE_PROMPT_SWITCH_FAILED = new(nameof(BATTLE_PROMPT_SWITCH_FAILED));
    public static readonly LocalizationKey BATTLE_PROMPT_MOVE_DAMAGE = new(nameof(BATTLE_PROMPT_MOVE_DAMAGE));
    public static readonly LocalizationKey BATTLE_PROMPT_PUT_COUNTER = new(nameof(BATTLE_PROMPT_PUT_COUNTER));
    public static readonly LocalizationKey BATTLE_PROMPT_PUT_COUNTERS = new(nameof(BATTLE_PROMPT_PUT_COUNTERS));
    public static readonly LocalizationKey BATTLE_PROMPT_MOVE_FROM = new(nameof(BATTLE_PROMPT_MOVE_FROM));
    public static readonly LocalizationKey BATTLE_PROMPT_MOVE_FROM_TO_WHICH = new(nameof(BATTLE_PROMPT_MOVE_FROM_TO_WHICH));
    public static readonly LocalizationKey BATTLE_PROMPT_MOVE_TO = new(nameof(BATTLE_PROMPT_MOVE_TO));
    public static readonly LocalizationKey BATTLE_PROMPT_PUT_ON = new(nameof(BATTLE_PROMPT_PUT_ON));
    public static readonly LocalizationKey BATTLE_PROMPT_BACK = new(nameof(BATTLE_PROMPT_BACK));
    public static readonly LocalizationKey BATTLE_PROMPT_WAITING = new(nameof(BATTLE_PROMPT_WAITING));
    public static readonly LocalizationKey BATTLE_PROMPT_LIFT_FAILED = new(nameof(BATTLE_PROMPT_LIFT_FAILED));
    public static readonly LocalizationKey BATTLE_PROMPT_CANT_PUT_THERE = new(nameof(BATTLE_PROMPT_CANT_PUT_THERE));
    public static readonly LocalizationKey BATTLE_PROMPT_NO_COUNTER = new(nameof(BATTLE_PROMPT_NO_COUNTER));
    public static readonly LocalizationKey BATTLE_PROMPT_CANT_MOVE_COUNTER = new(nameof(BATTLE_PROMPT_CANT_MOVE_COUNTER));
    public static readonly LocalizationKey BATTLE_PROMPT_HAS_DAMAGE = new(nameof(BATTLE_PROMPT_HAS_DAMAGE));
    public static readonly LocalizationKey BATTLE_PROMPT_MOVED_DAMAGE_TO = new(nameof(BATTLE_PROMPT_MOVED_DAMAGE_TO));
    public static readonly LocalizationKey BATTLE_PROMPT_PLACE_FAILED = new(nameof(BATTLE_PROMPT_PLACE_FAILED));
    public static readonly LocalizationKey BATTLE_PROMPT_STILL_PLACING = new(nameof(BATTLE_PROMPT_STILL_PLACING));
    public static readonly LocalizationKey BATTLE_PROMPT_NOT_ENOUGH_DAMAGE = new(nameof(BATTLE_PROMPT_NOT_ENOUGH_DAMAGE));
    public static readonly LocalizationKey BATTLE_PROMPT_PILE = new(nameof(BATTLE_PROMPT_PILE));
    public static readonly LocalizationKey BATTLE_PROMPT_PILE_SELECTED = new(nameof(BATTLE_PROMPT_PILE_SELECTED));
    public static readonly LocalizationKey BATTLE_PROMPT_LAST_LINE = new(nameof(BATTLE_PROMPT_LAST_LINE));
    #endregion

    #region Battle menu
    public static readonly LocalizationKey BATTLE_MENU = new(nameof(BATTLE_MENU));
    public static readonly LocalizationKey BATTLE_MENU_UNAVAILABLE = new(nameof(BATTLE_MENU_UNAVAILABLE));
    public static readonly LocalizationKey BATTLE_MENU_NO_OPTIONS = new(nameof(BATTLE_MENU_NO_OPTIONS));
    public static readonly LocalizationKey BATTLE_MENU_PERCENT = new(nameof(BATTLE_MENU_PERCENT));
    public static readonly LocalizationKey BATTLE_MENU_HELP = new(nameof(BATTLE_MENU_HELP));
    #endregion

    #region Match result
    public static readonly LocalizationKey BATTLE_RESULT_WON = new(nameof(BATTLE_RESULT_WON));
    public static readonly LocalizationKey BATTLE_RESULT_LOST = new(nameof(BATTLE_RESULT_LOST));
    public static readonly LocalizationKey BATTLE_RESULT_PLAYER_STATS = new(nameof(BATTLE_RESULT_PLAYER_STATS));
    public static readonly LocalizationKey BATTLE_RESULT_XP = new(nameof(BATTLE_RESULT_XP));
    public static readonly LocalizationKey BATTLE_RESULT_PASS_XP = new(nameof(BATTLE_RESULT_PASS_XP));
    public static readonly LocalizationKey BATTLE_RESULT_RANK_POINTS = new(nameof(BATTLE_RESULT_RANK_POINTS));
    public static readonly LocalizationKey BATTLE_RESULT_ITEM_TIMES = new(nameof(BATTLE_RESULT_ITEM_TIMES));
    public static readonly LocalizationKey BATTLE_RESULT_REWARDS = new(nameof(BATTLE_RESULT_REWARDS));
    public static readonly LocalizationKey BATTLE_RESULT_HELP = new(nameof(BATTLE_RESULT_HELP));
    #endregion

    #region Match intro and mulligans
    public static readonly LocalizationKey BATTLE_INTRO_OPPONENT = new(nameof(BATTLE_INTRO_OPPONENT));
    public static readonly LocalizationKey BATTLE_INTRO_VERSUS = new(nameof(BATTLE_INTRO_VERSUS));
    public static readonly LocalizationKey BATTLE_INTRO_VERSUS_RANK = new(nameof(BATTLE_INTRO_VERSUS_RANK));
    public static readonly LocalizationKey BATTLE_INTRO_YOU_RANK = new(nameof(BATTLE_INTRO_YOU_RANK));
    public static readonly LocalizationKey BATTLE_MULLIGANS = new(nameof(BATTLE_MULLIGANS));
    public static readonly LocalizationKey BATTLE_MULLIGAN_A_PLAYER = new(nameof(BATTLE_MULLIGAN_A_PLAYER));
    public static readonly LocalizationKey BATTLE_MULLIGAN_ONE = new(nameof(BATTLE_MULLIGAN_ONE));
    public static readonly LocalizationKey BATTLE_MULLIGAN_MANY = new(nameof(BATTLE_MULLIGAN_MANY));
    public static readonly LocalizationKey BATTLE_MULLIGAN_HIDDEN = new(nameof(BATTLE_MULLIGAN_HIDDEN));
    public static readonly LocalizationKey BATTLE_MULLIGAN_NTH = new(nameof(BATTLE_MULLIGAN_NTH));
    #endregion
    public static readonly LocalizationKey BATTLE_ZONE_OPP_LOST_ZONE = new(nameof(BATTLE_ZONE_OPP_LOST_ZONE));
    public static readonly LocalizationKey BATTLE_RPS_ROCK = new(nameof(BATTLE_RPS_ROCK));
    public static readonly LocalizationKey BATTLE_RPS_PAPER = new(nameof(BATTLE_RPS_PAPER));
    public static readonly LocalizationKey BATTLE_RPS_SCISSORS = new(nameof(BATTLE_RPS_SCISSORS));
    public static readonly LocalizationKey BATTLE_RPS_RESULT = new(nameof(BATTLE_RPS_RESULT));
    public static readonly LocalizationKey BATTLE_PROMPT_REFLIP_RESULT = new(nameof(BATTLE_PROMPT_REFLIP_RESULT));
    public static readonly LocalizationKey BATTLE_PROMPT_RANGE_OPTIONAL = new(nameof(BATTLE_PROMPT_RANGE_OPTIONAL));
    public static readonly LocalizationKey BATTLE_PROMPT_RANGE_BETWEEN = new(nameof(BATTLE_PROMPT_RANGE_BETWEEN));
    public static readonly LocalizationKey BATTLE_PROMPT_RANGE_EXACT = new(nameof(BATTLE_PROMPT_RANGE_EXACT));
    public static readonly LocalizationKey BATTLE_PROMPT_EXCLUSIVE_GROUPS = new(nameof(BATTLE_PROMPT_EXCLUSIVE_GROUPS));
    public static readonly LocalizationKey BATTLE_PROMPT_NOT_SELECTABLE = new(nameof(BATTLE_PROMPT_NOT_SELECTABLE));
    public static readonly LocalizationKey BATTLE_PROMPT_HIDDEN_CARDS = new(nameof(BATTLE_PROMPT_HIDDEN_CARDS));
    public static readonly LocalizationKey BATTLE_PROMPT_NOTHING_SELECTED = new(nameof(BATTLE_PROMPT_NOTHING_SELECTED));
    public static readonly LocalizationKey BATTLE_PROMPT_MAX_SELECTED = new(nameof(BATTLE_PROMPT_MAX_SELECTED));
    public static readonly LocalizationKey BATTLE_ATTACHED_CARD_TEXT = new(nameof(BATTLE_ATTACHED_CARD_TEXT));
    public static readonly LocalizationKey BATTLE_ATTACK_CURRENT_DAMAGE = new(nameof(BATTLE_ATTACK_CURRENT_DAMAGE));
    public static readonly LocalizationKey BATTLE_REVEALED_CARDS = new(nameof(BATTLE_REVEALED_CARDS));
    public static readonly LocalizationKey BATTLE_TIME_ACTION_WARNING = new(nameof(BATTLE_TIME_ACTION_WARNING));
    public static readonly LocalizationKey BATTLE_TIME_CLOCK_WARNING = new(nameof(BATTLE_TIME_CLOCK_WARNING));
    public static readonly LocalizationKey BATTLE_STATUS_OPPONENT = new(nameof(BATTLE_STATUS_OPPONENT));
    public static readonly LocalizationKey BATTLE_STATUS_TURN = new(nameof(BATTLE_STATUS_TURN));
    public static readonly LocalizationKey BATTLE_STATUS_ACTION_TIME = new(nameof(BATTLE_STATUS_ACTION_TIME));
    public static readonly LocalizationKey BATTLE_STATUS_MY_CLOCK = new(nameof(BATTLE_STATUS_MY_CLOCK));
    public static readonly LocalizationKey BATTLE_STATUS_OPP_CLOCK = new(nameof(BATTLE_STATUS_OPP_CLOCK));
    public static readonly LocalizationKey BATTLE_TOKEN_VSTAR = new(nameof(BATTLE_TOKEN_VSTAR));
    public static readonly LocalizationKey BATTLE_TOKEN_GX = new(nameof(BATTLE_TOKEN_GX));
    public static readonly LocalizationKey BATTLE_TOKEN_MINE_AVAILABLE = new(nameof(BATTLE_TOKEN_MINE_AVAILABLE));
    public static readonly LocalizationKey BATTLE_TOKEN_MINE_USED = new(nameof(BATTLE_TOKEN_MINE_USED));
    public static readonly LocalizationKey BATTLE_TOKEN_OPP_AVAILABLE = new(nameof(BATTLE_TOKEN_OPP_AVAILABLE));
    public static readonly LocalizationKey BATTLE_TOKEN_OPP_USED = new(nameof(BATTLE_TOKEN_OPP_USED));
    public static readonly LocalizationKey BATTLE_EFFECTS_MINE = new(nameof(BATTLE_EFFECTS_MINE));
    public static readonly LocalizationKey BATTLE_EFFECTS_OPP = new(nameof(BATTLE_EFFECTS_OPP));
    public static readonly LocalizationKey BATTLE_EFFECTS_NONE = new(nameof(BATTLE_EFFECTS_NONE));
    public static readonly LocalizationKey BATTLE_LOG_TITLE = new(nameof(BATTLE_LOG_TITLE));
    public static readonly LocalizationKey BATTLE_LOG_EMPTY = new(nameof(BATTLE_LOG_EMPTY));
    public static readonly LocalizationKey BATTLE_LOG_HELP = new(nameof(BATTLE_LOG_HELP));
    public static readonly LocalizationKey BATTLE_REVIEW_HELP = new(nameof(BATTLE_REVIEW_HELP));
    public static readonly LocalizationKey BATTLE_EMOTE_MENU = new(nameof(BATTLE_EMOTE_MENU));
    public static readonly LocalizationKey BATTLE_EMOTE_RECEIVED = new(nameof(BATTLE_EMOTE_RECEIVED));
    public static readonly LocalizationKey BATTLE_EMOTE_MUTE = new(nameof(BATTLE_EMOTE_MUTE));
    public static readonly LocalizationKey BATTLE_EMOTE_UNMUTE = new(nameof(BATTLE_EMOTE_UNMUTE));
    public static readonly LocalizationKey BATTLE_EMOTE_MUTED = new(nameof(BATTLE_EMOTE_MUTED));
    public static readonly LocalizationKey BATTLE_EMOTE_UNMUTED = new(nameof(BATTLE_EMOTE_UNMUTED));
    public static readonly LocalizationKey BATTLE_EMOTE_UNAVAILABLE = new(nameof(BATTLE_EMOTE_UNAVAILABLE));
}
