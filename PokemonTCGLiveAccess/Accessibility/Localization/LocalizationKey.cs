namespace Accessibility;

// Every string the mod speaks in its own words has a key here, and a line with that key in
// Localization/<language>/<Feature>.txt (as Hearthstone Access' ACCESSIBILITY.txt). Features declare their keys
// in their own partial file (LocalizationKey.Battle.cs, ...) next to a matching <Feature>.txt, so translators
// work feature by feature. The test project fails when a key has no English line, a line has no key, or a
// translation loses a {0} placeholder.
public sealed partial class LocalizationKey
{
    public string Key { get; }

    private LocalizationKey(string key) => Key = key;

    public override string ToString() => Key;

    #region Global (Global.txt)
    public static readonly LocalizationKey GLOBAL_MOD_LOADED = new(nameof(GLOBAL_MOD_LOADED));
    public static readonly LocalizationKey GLOBAL_ACCESSIBILITY_ON = new(nameof(GLOBAL_ACCESSIBILITY_ON));
    public static readonly LocalizationKey GLOBAL_ACCESSIBILITY_OFF = new(nameof(GLOBAL_ACCESSIBILITY_OFF));
    public static readonly LocalizationKey GLOBAL_PLEASE_WAIT = new(nameof(GLOBAL_PLEASE_WAIT));
    public static readonly LocalizationKey GLOBAL_HELP = new(nameof(GLOBAL_HELP));
    public static readonly LocalizationKey GLOBAL_COMPAT_PROBLEMS = new(nameof(GLOBAL_COMPAT_PROBLEMS));
    public static readonly LocalizationKey GLOBAL_CHECKED = new(nameof(GLOBAL_CHECKED));
    public static readonly LocalizationKey GLOBAL_UNCHECKED = new(nameof(GLOBAL_UNCHECKED));
    public static readonly LocalizationKey GLOBAL_DISABLED = new(nameof(GLOBAL_DISABLED));
    public static readonly LocalizationKey GLOBAL_SELECTED = new(nameof(GLOBAL_SELECTED));
    #endregion

    #region Menus (Global.txt)
    public static readonly LocalizationKey MENU_OPTION_FORMAT = new(nameof(MENU_OPTION_FORMAT));
    public static readonly LocalizationKey MENU_NO_ITEMS = new(nameof(MENU_NO_ITEMS));
    public static readonly LocalizationKey MENU_HELP = new(nameof(MENU_HELP));
    public static readonly LocalizationKey MENU_HELP_WITH_BACK = new(nameof(MENU_HELP_WITH_BACK));
    #endregion

    #region Card symbols (Global.txt)
    public static readonly LocalizationKey ENERGY_COLORLESS = new(nameof(ENERGY_COLORLESS));
    public static readonly LocalizationKey ENERGY_FIRE = new(nameof(ENERGY_FIRE));
    public static readonly LocalizationKey ENERGY_GRASS = new(nameof(ENERGY_GRASS));
    public static readonly LocalizationKey ENERGY_WATER = new(nameof(ENERGY_WATER));
    public static readonly LocalizationKey ENERGY_FIGHTING = new(nameof(ENERGY_FIGHTING));
    public static readonly LocalizationKey ENERGY_FAIRY = new(nameof(ENERGY_FAIRY));
    public static readonly LocalizationKey ENERGY_DARKNESS = new(nameof(ENERGY_DARKNESS));
    public static readonly LocalizationKey ENERGY_LIGHTNING = new(nameof(ENERGY_LIGHTNING));
    public static readonly LocalizationKey ENERGY_DRAGON = new(nameof(ENERGY_DRAGON));
    public static readonly LocalizationKey ENERGY_METAL = new(nameof(ENERGY_METAL));
    public static readonly LocalizationKey ENERGY_PSYCHIC = new(nameof(ENERGY_PSYCHIC));
    public static readonly LocalizationKey MARKER_MEGA = new(nameof(MARKER_MEGA));
    public static readonly LocalizationKey MARKER_PRISM_STAR = new(nameof(MARKER_PRISM_STAR));
    public static readonly LocalizationKey MARKER_TAG_TEAM = new(nameof(MARKER_TAG_TEAM));
    #endregion
}
