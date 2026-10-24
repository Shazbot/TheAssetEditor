using GameWorld.Core.Rendering.Geometry;
using GameWorld.Core.Services.SceneSaving.Lod.MeshDecimatorIntegration;

namespace GameWorld.Core.Services.SceneSaving.Lod.MeshOptimizerIntegration;

/// <summary>Runs meshoptimizer with a per-mesh fallback to the managed decimator.</summary>
public static class MeshReduction
{
    private static int _nativeLoadWarningLogged;

    public static MeshObject Reduce(MeshObject original, float factor, string meshName, ILogger logger)
        => Reduce(original, factor, meshName, logger, (mesh, reductionFactor) => MeshOptReducer.Reduce(mesh, reductionFactor));

    internal static MeshObject Reduce(
        MeshObject original,
        float factor,
        string meshName,
        ILogger logger,
        Func<MeshObject, float, MeshOptReducerResult> meshOptReducer)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(meshOptReducer);
        if (!float.IsFinite(factor) || factor < 0)
            throw new ArgumentOutOfRangeException(nameof(factor), "The reduction factor must be finite and non-negative.");

        MeshOptReducerResult meshOptResult;
        try
        {
            meshOptResult = meshOptReducer(original, factor);
        }
        catch (Exception exception)
        {
            var isNativeLoadFailure = exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException;
            if (isNativeLoadFailure)
            {
                if (Interlocked.Exchange(ref _nativeLoadWarningLogged, 1) == 0)
                    logger.Here().Warning($"meshoptimizer could not be loaded while reducing mesh '{meshName}'; using the managed decimator. {exception}");
            }
            else
            {
                logger.Here().Warning($"meshoptimizer failed while reducing mesh '{meshName}'; using the managed decimator. {exception}");
            }

            var sourceTriangleCount = original.IndexArray.Length / 3;
            var requestedTriangleCount = CalculateFallbackTargetTriangleCount(sourceTriangleCount, factor);
            MeshObject reducedMesh;
            var path = "fallback";
            try
            {
                reducedMesh = DecimatorMeshOptimizer.GetReducedMeshCopy(original, factor);
            }
            catch (Exception fallbackException)
            {
                logger.Here().Error($"Managed mesh decimation also failed for mesh '{meshName}'; leaving the mesh unchanged. {fallbackException}");
                reducedMesh = MeshOptReducer.GetReducedMeshCopy(original, 1.0f);
                path = "fallback-unchanged";
            }

            var achievedTriangleCount = reducedMesh.IndexArray.Length / 3;
            var targetReached = achievedTriangleCount <= requestedTriangleCount;
            var limitReason = targetReached ? "none" : "topology";
            LogResult(logger, meshName, sourceTriangleCount, requestedTriangleCount, achievedTriangleCount,
                null, false, targetReached, limitReason, path);
            return reducedMesh;
        }

        LogResult(logger, meshName, meshOptResult.Stats.SourceTriangleCount, meshOptResult.Stats.RequestedTriangleCount,
            meshOptResult.Stats.AchievedTriangleCount, meshOptResult.Stats.ResultErrorRelative, meshOptResult.Stats.ErrorLimited,
            meshOptResult.Stats.TargetReached, meshOptResult.Stats.LimitReason, "meshopt");
        return meshOptResult.Mesh;
    }

    private static int CalculateFallbackTargetTriangleCount(int sourceTriangleCount, float factor)
    {
        if (factor >= 1)
            return sourceTriangleCount;

        return Math.Max(1, (int)Math.Ceiling(sourceTriangleCount * (double)factor));
    }

    private static void LogResult(ILogger logger, string meshName, int sourceTriangles, int requestedTriangles,
        int achievedTriangles, float? resultErrorRelative, bool errorLimited, bool targetReached, string limitReason, string path)
    {
        var error = resultErrorRelative is float relativeError ? relativeError.ToString("G6") : "n/a";
        var loggedLimitReason = limitReason == "topology" ? "topology/locked-border" : limitReason;
        var message =
            $"Mesh reduction: name='{meshName}', source={sourceTriangles} tris, requested={requestedTriangles} tris, achieved={achievedTriangles} tris, relativeError={error}, TargetReached={targetReached}, ErrorLimited={errorLimited}, LimitReason={loggedLimitReason}, path={path}";
        if (!targetReached && requestedTriangles > 0 && achievedTriangles > requestedTriangles * 1.25d)
            logger.Here().Warning(message);
        else
            logger.Here().Information(message);
    }
}
