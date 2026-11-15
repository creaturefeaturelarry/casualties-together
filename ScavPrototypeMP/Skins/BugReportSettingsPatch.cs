// SelectTab rebuilds the rows. put the bug report back after that.
public static class BugReportSettingsPatch {
    static void Prefix() {
        BugReportSettings.Remove();
    }

    static void Postfix(int index) {
        BugReportSettings.AddIfOurCategory(index);
    }
}