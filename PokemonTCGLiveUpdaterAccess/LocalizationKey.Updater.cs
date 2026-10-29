namespace Accessibility;

// Updater.txt: the standalone updater (Updater.exe). Everything else it says is the updater's own text.
public sealed partial class LocalizationKey
{
    #region Updater
    public static readonly LocalizationKey UPDATER_HELP = new(nameof(UPDATER_HELP));
    public static readonly LocalizationKey UPDATER_PROGRESS_FORMAT = new(nameof(UPDATER_PROGRESS_FORMAT));
    #endregion
}
