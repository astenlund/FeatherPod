using System.Configuration;

namespace FeatherPod;

/// <summary>
/// User settings stored in AppData/Local/FeatherPod.Cli/user.config
/// </summary>
public sealed class UserSettings : ApplicationSettingsBase
{
    public static UserSettings Default { get; } = (UserSettings)Synchronized(new UserSettings());

    [UserScopedSetting]
    [DefaultSettingValue("")]
    public string LastUsedFeedDev
    {
        get => (string)this[nameof(LastUsedFeedDev)];
        set => this[nameof(LastUsedFeedDev)] = value;
    }

    [UserScopedSetting]
    [DefaultSettingValue("")]
    public string LastUsedFeedTest
    {
        get => (string)this[nameof(LastUsedFeedTest)];
        set => this[nameof(LastUsedFeedTest)] = value;
    }

    [UserScopedSetting]
    [DefaultSettingValue("")]
    public string LastUsedFeedProd
    {
        get => (string)this[nameof(LastUsedFeedProd)];
        set => this[nameof(LastUsedFeedProd)] = value;
    }

    public string GetLastUsedFeed(string environment)
    {
        return environment switch
        {
            "Dev" => LastUsedFeedDev,
            "Test" => LastUsedFeedTest,
            "Prod" => LastUsedFeedProd,
            _ => string.Empty
        };
    }

    public void SetLastUsedFeed(string environment, string feedId)
    {
        switch (environment)
        {
            case "Dev":
                LastUsedFeedDev = feedId;
                break;
            case "Test":
                LastUsedFeedTest = feedId;
                break;
            case "Prod":
                LastUsedFeedProd = feedId;
                break;
        }
        Save();
    }
}
