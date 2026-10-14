namespace TcpRouteTray;

internal static class PowerShellOutputSelfTest
{
    public static void Run()
    {
        const string progress = "<Objs xmlns=\"http://schemas.microsoft.com/powershell/2004/04\"><Obj S=\"progress\"><S>broken progress text</S></Obj></Objs>";
        Expect(PowerShellOutput.CleanError("#< CLIXML\r\nInterface unavailable\r\n" + progress) == "Interface unavailable", "Mixed CLIXML leaked into the error");
        Expect(PowerShellOutput.CleanError("#< CLIXML\n" + progress) == "PowerShell network configuration failed.", "Progress-only CLIXML became an error message");
        const string errors = "<Objs xmlns=\"http://schemas.microsoft.com/powershell/2004/04\"><S S=\"Error\">接口不可用_x000D__x000A_</S><S S=\"Error\">请选择其他接口</S><S S=\"Warning\">ignored</S></Objs>";
        Expect(PowerShellOutput.CleanError("#< CLIXML\n" + errors) == "接口不可用\r\n请选择其他接口", "Serialized Unicode errors were not decoded");
        Expect(PowerShellOutput.CleanError("#< CLIXML\n<Objs broken") == "PowerShell network configuration failed.", "Malformed CLIXML leaked into the error");
        Expect(PowerShellOutput.CleanError(" readable error ") == "readable error", "Plain errors changed");
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
