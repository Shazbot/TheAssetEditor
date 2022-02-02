﻿using CommonControls.Common;
using CommonControls.Services;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Graphics;
using MonoGame.Framework.WpfInterop;
using Pfim;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using CommonControls.FileTypes.RigidModel.Types;
using SharpDX.DirectWrite;
using View3D.Components;
using ImageFormat = Pfim.ImageFormat;

namespace View3D.Utility
{
    public enum ShaderTypes
    {
        Line,
        Mesh,
        TexturePreview,
        Phazer,
        BasicEffect,
        GeometryInstance,
    }

    public partial class ResourceLibary : BaseComponent, IDisposable
    {
        ILogger _logger = Logging.Create<ResourceLibary>();

        Dictionary<string, Texture2D> _textureMap = new Dictionary<string, Texture2D>();
        Dictionary<ShaderTypes, Effect> _shaders = new Dictionary<ShaderTypes, Effect>();

        public PackFileService Pfs { get; private set; }
        public ContentManager Content { get; set; }
        public SpriteBatch CommonSpriteBatch{ get; private set; }
        public SpriteFont DefaultFont { get; private set; }

        public TextureCube PbrDiffuse { get; private set; }
        public TextureCube PbrSpecular { get; private set; }
        public Texture2D PbrLut { get; private set; }

        WpfGame _game;
        public ResourceLibary(WpfGame game, PackFileService pf) : base(game)
        {
            Pfs = pf;
            _game = game;
        }

        public SpriteFont LoadFont(string path)
        {
            return Content.Load<SpriteFont>(path);
        }

        public override void Initialize()
        {
            Content = _game.Content;

            // Load default shaders
            LoadEffect("Shaders\\Phazer\\main", ShaderTypes.Phazer);
            LoadEffect("Shaders\\Geometry\\BasicShader", ShaderTypes.BasicEffect);
            LoadEffect("Shaders\\TexturePreview", ShaderTypes.TexturePreview);
            LoadEffect("Shaders\\LineShader", ShaderTypes.Line);

            DefaultFont = LoadFont("Fonts//DefaultFont");
            CommonSpriteBatch = new SpriteBatch(_game.GraphicsDevice);

            PbrDiffuse = Content.Load<TextureCube>("textures\\phazer\\DIFFUSE_irr_qwantani_rgba32f");
            PbrSpecular = Content.Load<TextureCube>("textures\\phazer\\SkyOnly_SpecularHDR");   // Skyonly
            PbrLut = Content.Load<Texture2D>("textures\\phazer\\Brdf_rgba32f_raw");
        }


        public Texture2D LoadTexture(string fileName, bool skipCache = false, TexureType texureType = TexureType.Diffuse)
        {
            if (_textureMap.ContainsKey(fileName) && !skipCache)
                return _textureMap[fileName];

            var isOnFileSystem = File.Exists(fileName);

            var texture = LoadTextureAsTexture2d(fileName, _game.GraphicsDevice, new ImageInformation(), isOnFileSystem, texureType);
            if (texture != null)
                _textureMap[fileName] = texture;

            return texture;
        }

        public Texture2D ForceLoadImage(string fileName, out ImageInformation imageInfo)
        {
            imageInfo = new ImageInformation();
            return LoadTextureAsTexture2d(fileName, _game.GraphicsDevice, imageInfo, File.Exists(fileName));
        }

        public void SaveTexture(Texture2D texture, string path)
        {
            using (FileStream stream = new FileStream(path, FileMode.OpenOrCreate))
            {
                texture.SaveAsPng(stream, texture.Width, texture.Height);
            }
        }

        Texture2D LoadTextureAsTexture2d(string fileName, GraphicsDevice device, ImageInformation out_imageInfo, bool isOnFileSystem, TexureType texureType=TexureType.Diffuse)
        {
            var file = Pfs.FindFile(fileName);

            if (isOnFileSystem && Path.GetExtension(fileName) == ".png")
            {
                // WaitCursor can only be used in a STA thread
                using (var waitCursor = Thread.CurrentThread.GetApartmentState() == ApartmentState.STA ? new WaitCursor() : null)
                {
                    SaveHelper.SavePNGTextureAsDDS(fileName, texureType);
                }
            }

            if (file == null && !isOnFileSystem)
            {
                _logger.Here().Error($"Unable to find texture: {fileName}");
                return null;
            }
            try
            {
                var fileNameAsDDS = Path.ChangeExtension(fileName, ".dds");
                if (File.Exists(fileNameAsDDS))
                {
                    fileName = fileNameAsDDS;
                }

                byte[] content = isOnFileSystem ? File.ReadAllBytes(fileName) : file.DataSource.ReadData();

                using (MemoryStream stream = new MemoryStream(content))
                {
                    using var image = Pfim.Pfim.FromStream(stream);
                    out_imageInfo.SetFromImage(image);

                    if (image.Format != ImageFormat.Rgba32)
                    {
                        _logger.Here().Error($"Error loading texture ({fileName} - Unkown textur format {image.Format})");
                        return null;
                    }

                    var texture = new Texture2D(device, image.Width, image.Height, true, SurfaceFormat.Bgra32);
                    texture.SetData(0, null, image.Data, 0, image.DataLen);

                    // Load mipmaps
                    for (int i = 0; i < image.MipMaps.Length; i++)
                    {
                        try
                        {
                            var mipmap = image.MipMaps[i];
                            if (mipmap.Width > 4)
                                texture.SetData(i + 1, null, image.Data, mipmap.DataOffset, mipmap.DataLen);
                        }
                        catch 
                        {
                            _logger.Here().Warning($"Error loading Mipmap [{i}]");
                        }
                    }

                    return texture;
                }
            }
            catch (Exception e)
            {
                _logger.Here().Error($"Error loading texture {fileName} - {e.Message})");
                return null;
            }
        }

        public Effect LoadEffect(string fileName, ShaderTypes type)
        {
            if (_shaders.ContainsKey(type))
                return _shaders[type];
            var effect = Content.Load<Effect>(fileName);
            _shaders[type] = effect;
            return effect;
        }

        public Effect GetEffect(ShaderTypes type)
        {
            if (_shaders.ContainsKey(type))
                return _shaders[type].Clone();
            throw new Exception($"Shader not found: ShaderTypes::{type}");
        }

        public Effect GetStaticEffect(ShaderTypes type)
        {
            if (_shaders.ContainsKey(type))
                return _shaders[type];
            throw new Exception($"Shader not found: ShaderTypes::{type}");
        }

        public void Dispose()
        {
            foreach (var item in _textureMap)
                item.Value.Dispose();
            _textureMap.Clear();

            foreach (var item in _shaders)
                item.Value.Dispose();
            _shaders.Clear();


            //Content.Dispose();
            Content = null;

            PbrDiffuse.Dispose();
            PbrDiffuse = null;


            PbrSpecular.Dispose();
            PbrSpecular = null;


            PbrLut.Dispose();
            PbrLut = null;

            CommonSpriteBatch.Dispose();
            CommonSpriteBatch = null;
        }
    }
}
