namespace AxmolHub.Core;

public enum UserFeedbackKind
{
    Success,
    Info,
    Warning,
    Error,
}

public readonly record struct UserFeedback(string Text, UserFeedbackKind Kind, string Key)
{
    public static UserFeedback Success(string text, string key) => new(text, UserFeedbackKind.Success, key);
    public static UserFeedback Info(string text, string key) => new(text, UserFeedbackKind.Info, key);
    public static UserFeedback Warning(string text, string key) => new(text, UserFeedbackKind.Warning, key);
    public static UserFeedback Error(string text, string key) => new(text, UserFeedbackKind.Error, key);
}

public readonly record struct GitActionOutcome(bool Success, UserFeedback Feedback);
