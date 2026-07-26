using GameWorld.Core.Services;
using Microsoft.Xna.Framework.Graphics;

namespace GameWorld.Core.Components.Rendering
{
    internal class RenderTargetHelper
    {
        public static RenderTarget2D GetRenderTarget(GraphicsDevice device, RenderTarget2D existingRenderTarget, float imageUpScale, IGraphicsResourceCreator graphicsResourceCreator, int? widthOverride = null, int? heightOverride = null)
        {
            var width = (int)((widthOverride ?? device.Viewport.Width) * imageUpScale);
            var height = (int)((heightOverride ?? device.Viewport.Height) * imageUpScale);

            if (existingRenderTarget == null)
            {
                return graphicsResourceCreator.CreateRenderTarget2D(width, height, false, SurfaceFormat.Color, DepthFormat.Depth24, 8, RenderTargetUsage.PreserveContents);
            }

            if (existingRenderTarget.Width == width && existingRenderTarget.Height == height)
                return existingRenderTarget;

            graphicsResourceCreator.DisposeTracked(existingRenderTarget);
            return graphicsResourceCreator.CreateRenderTarget2D(width, height, false, SurfaceFormat.Color, DepthFormat.Depth24, 8, RenderTargetUsage.PreserveContents);
        }
    }
}
