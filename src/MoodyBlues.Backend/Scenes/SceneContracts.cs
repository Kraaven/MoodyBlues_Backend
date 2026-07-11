namespace MoodyBlues.Backend.Scenes;

public sealed record RenameSceneRequest(string? DisplayName);

public sealed record SceneResponse(string SceneId, string? DisplayName, DateTime UpdatedAtUtc);
