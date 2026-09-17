namespace GameWorld.Core.Animation;

/// <summary>
/// Minimal playback boundary used by <see cref="GameSkeleton"/>.
/// The editor's WPF playback controller implements this interface, while
/// headless consumers can pass null and use the bind pose directly.
/// </summary>
public interface IAnimationPlayer
{
    AnimationFrame? GetCurrentAnimationFrame();
}
