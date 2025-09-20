// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Textures;
using osu.Framework.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Veldrid;
using PixelFormat = Veldrid.PixelFormat;
using Texture = Veldrid.Texture;

namespace osu.Framework.Graphics.Veldrid.Textures
{
    internal class VeldridNativeTexture : IVeldridTexture
    {
        IRenderer INativeTexture.Renderer => Renderer;

        public string Identifier
        {
            get
            {
                if (!Available || resources == null)
                    return "-";

                return resources.Texture.Name;
            }
        }

        public int MaxSize => Renderer.MaxTextureSize;

        public int Width { get; set; }
        public int Height { get; set; }

        public bool Available { get; private set; } = true;

        ulong INativeTexture.TotalBindCount { get; set; }

        public bool BypassTextureUploadQueueing { get; set; } = false;

        public int? MipLevel
        {
            get => 0;
            set { } // The value never changes
        }
        public bool UploadComplete => false;

        public Texture VeldridTexture { get; private set; }

        protected readonly IVeldridRenderer Renderer;

        private bool isDisposed;
        private VeldridTextureResources? resources;
        private readonly VeldridTextureResources[] resourcesArray;

        public VeldridNativeTexture(
            IVeldridRenderer renderer,
            uint width,
            uint height,
            PixelFormat format,
            TextureUsage textureUsage = TextureUsage.Sampled
        )
        {
            Renderer = renderer;
            Width = (int) width;
            Height = (int) height;

            // Initialize texture resources
            var texture = renderer.Factory.CreateTexture(TextureDescription.Texture2D(
                width,
                height,
                1,
                1,
                format,
                textureUsage
            ));

            VeldridTexture = texture;

            var samplerDescription = new SamplerDescription
            {
                AddressModeU = SamplerAddressMode.Clamp,
                AddressModeV = SamplerAddressMode.Clamp,
                AddressModeW = SamplerAddressMode.Clamp,
                Filter = SamplerFilter.MinLinearMagLinearMipLinear,
                MinimumLod = (uint)MipLevel!,
                MaximumLod = (uint)MipLevel,
                MaximumAnisotropy = 0
            };

            var sampler = renderer.Factory.CreateSampler(ref samplerDescription);

            resources = new VeldridTextureResources(texture, sampler);
            resourcesArray = [resources];
        }

        public void FlushUploads()
        {
            // Nothing to do
        }

        public void SetData(ITextureUpload upload)
        {
            // Nothing to do
        }

        public bool Upload()
        {
            // Nothing to do
            return false;
        }

        public Image<Rgba32> ExtractData<TPixel>(bool flipVertical = false) where TPixel : unmanaged, IPixel<TPixel>
        {
            unsafe
            {
                var texture = resources!.Texture;

                uint width = texture.Width;
                uint height = texture.Height;

                using var staging = Renderer.Factory.CreateTexture(TextureDescription.Texture2D(width, height, texture.MipLevels, texture.ArrayLayers, texture.Format, TextureUsage.Staging));
                using var commands = Renderer.Factory.CreateCommandList();
                using var fence = Renderer.Factory.CreateFence(false);

                commands.Begin();
                commands.CopyTexture(texture, staging);
                commands.End();
                Renderer.Device.SubmitCommands(commands, fence);

                if (!WaitForFence(fence, 5000))
                {
                    Logger.Log("Failed to capture framebuffer content within reasonable time.", level: LogLevel.Important);
                    return new Image<Rgba32>((int)width, (int)height);
                }

                var resource = Renderer.Device.Map(staging, MapMode.Read);
                var span = new Span<TPixel>(resource.Data.ToPointer(), (int)(resource.SizeInBytes / Marshal.SizeOf<TPixel>()));

                // on some backends (Direct3D11, in particular), the staging resource data may contain padding at the end of each row for alignment,
                // which means that for the image width, we cannot use the framebuffer's width raw.
                using var image = Image.LoadPixelData<TPixel>(span, (int)(resource.RowPitch / Marshal.SizeOf<TPixel>()), (int)height);

                if (flipVertical)
                    image.Mutate(i => i.Flip(FlipMode.Vertical));

                // if the image width doesn't match the framebuffer, it means that we still have padding at the end of each row mentioned above to get rid of.
                // snip it to get a clean image.
                if (image.Width != width)
                    image.Mutate(i => i.Crop((int)texture.Width, (int)texture.Height));

                Renderer.Device.Unmap(staging);

                return image.CloneAs<Rgba32>();

                bool WaitForFence(Fence fenceToWait, int millisecondsTimeout)
                {
                    return Renderer.Device.WaitForFence(fenceToWait, (ulong)(millisecondsTimeout * 1_000_000));
                }
            }
        }

        public virtual int GetByteSize() => Width * Height * 4;

        public IReadOnlyList<VeldridTextureResources> GetResourceList()
        {
            return resourcesArray;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool isDisposing)
        {
            if (isDisposed)
                return;

            isDisposed = true;

            Renderer.ScheduleDisposal(texture =>
            {

                texture.resources?.Dispose();
                texture.resources = null;

                texture.Available = false;
            }, this);
        }
    }
}
