﻿using CommonControls.Common;
using Microsoft.Xna.Framework;
using MonoGame.Framework.WpfInterop;
using Serilog;
using System;
using System.Collections.Generic;
using System.Text;
using View3D.Components.Rendering;
using View3D.Rendering;
using View3D.Rendering.RenderItems;
using View3D.Utility;

namespace View3D.Components.Component
{
    public class GridComponent : BaseComponent, IDisposable
    {
        ILogger _logger = Logging.Create<GridComponent>();

        RenderEngineComponent _renderComponent;
        LineMeshRender _gridMesh;

        public GridComponent(IComponentManager componentManager) : base(componentManager)
        {
        }

        public override void Initialize()
        {
            _renderComponent = ComponentManager.GetComponent<RenderEngineComponent>();
            var resourceLib = ComponentManager.GetComponent<ResourceLibary>();
            _gridMesh = new LineMeshRender(resourceLib);
            _gridMesh.CreateGrid();

            base.Initialize();
        }

        public override void Draw(GameTime gameTime)
        {
            _renderComponent.AddRenderItem(RenderBuckedId.Line, new LineRenderItem() { LineMesh = _gridMesh, ModelMatrix = Matrix.Identity });
            base.Draw(gameTime);
        }

        public void Dispose()
        {
            _gridMesh.Dispose();
            _gridMesh = null;
        }
    }
}
