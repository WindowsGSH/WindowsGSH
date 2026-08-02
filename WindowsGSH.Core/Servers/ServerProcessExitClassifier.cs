namespace WindowsGSH.Core.Servers;

public enum ServerProcessExitDecision
{
    Ignore,
    CleanExit,
    UnexpectedExit
}

public static class ServerProcessExitClassifier
{
    public static ServerProcessExitDecision Classify(
        bool isExpectedExit,
        bool isAppClosing,
        int? exitCode,
        bool isWithinBootGracePeriod)
    {
        if (isExpectedExit || isAppClosing)
        {
            return ServerProcessExitDecision.Ignore;
        }

        if (exitCode == 0 && !isWithinBootGracePeriod)
        {
            return ServerProcessExitDecision.CleanExit;
        }

        return ServerProcessExitDecision.UnexpectedExit;
    }
}
