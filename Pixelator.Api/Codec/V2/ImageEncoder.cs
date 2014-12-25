﻿using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using Pixelator.Api.Codec.Imaging;
using Pixelator.Api.Codec.Layout;
using Pixelator.Api.Codec.Layout.Serialization;
using Pixelator.Api.Codec.Streams;
using Pixelator.Api.Configuration;

namespace Pixelator.Api.Codec.V2
{
    internal class ImageEncoder : V1.ImageEncoder
    {
        private readonly PixelStorageCalculator _pixelStorageCalculator = new PixelStorageCalculator();

        public ImageEncoder(EncodingConfiguration encodingConfiguration) : base(encodingConfiguration)
        {
        }

        public override Version Version
        {
            get { return Version.V2; }
        }

        protected override void ValidateConfiguration(ImageConfiguration configuration)
        {
        }

        private ImageDimensions CalculateImageDimensions(
            Imaging.ImageFormat format, 
            PixelStorageOptions storageOptions, 
            ImageDimensions embeddedImageDimensions,
            long totalBytes,
            out int imageRepeats)
        {
            imageRepeats = 1;

            int bitsPerPixel = storageOptions == null
                ? format.BytesPerPixel * 8
                : storageOptions.BitsPerPixel;
            long pixelsRequired = (long)Math.Ceiling(totalBytes / (bitsPerPixel / 8.0));

            int frames;
            if (!format.SupportsFrames)
            {
                frames = 1;
            }
            else if (embeddedImageDimensions != null)
            {
                frames = embeddedImageDimensions.Frames ?? 1;
            }
            else
            {
                frames = (int)Math.Ceiling(pixelsRequired / Math.Pow(ImageWidthFrameThreshold, 2));
            }

            int imageWidth; 
            int imageHeight;
            if (embeddedImageDimensions != null)
            {
                imageWidth = embeddedImageDimensions.Width;
                imageHeight = embeddedImageDimensions.Height;
            }
            else
            {
                imageWidth = (int)Math.Floor(Math.Sqrt(pixelsRequired / frames));
                imageHeight = (int)(pixelsRequired / (imageWidth * frames));
            }

            while (Math.BigMul(imageHeight, (int)Math.Floor(imageWidth * (bitsPerPixel / 8.0) * frames)) <= totalBytes)
            {
                if (format.SupportsFrames)
                {
                    imageRepeats++;
                    frames += (embeddedImageDimensions != null ? embeddedImageDimensions.Frames : null) ?? 1;
                }
                else
                {
                    imageHeight++;
                }
            }

            return new ImageDimensions(format.SupportsFrames ? frames : (int?)null, imageWidth, imageHeight);
        }

        protected override async Task<Stream> CreateImageWriterStreamAsync(ImageConfiguration configuration, Stream output, long totalBytes)
        {
            Imaging.ImageFormat imageFormat = ImageFormatFactory.GetFormat(configuration.Format);

            ImageDimensions embeddedImageDimensions = null;
            PixelStorageOptions pixelStorageOptions;
            Stream embeddedImageStream = null;
            if (configuration.HasEmbeddedImage)
            {
                embeddedImageDimensions = new ImageDimensions(configuration.EmbeddedImage.Image);
                pixelStorageOptions = _pixelStorageCalculator.CalculatePixelStorageOptions(
                    imageFormat,
                    configuration.EmbeddedImage.EmbeddedPixelStorage,
                    embeddedImageDimensions,
                    totalBytes);

                embeddedImageStream = imageFormat.LoadPixelDataStream(configuration.EmbeddedImage.Image);
            }
            else
            {
                pixelStorageOptions = imageFormat.PixelStorageWithBitsPerChannel(
                    8,
                    PixelStorageOptions.BitStorageMode.MostSignificantBits);
            }

            byte[] pixelStorageBytes = await new PixelStorageOptionsSerializer().SerializeToBytesAsync(pixelStorageOptions);
            totalBytes += pixelStorageBytes.Length;

            int embeddedImageRepeats;

            ImageOptions imageOptions = GenerateImageOptions(
                configuration,
                imageFormat,
                CalculateImageDimensions(imageFormat, pixelStorageOptions, embeddedImageDimensions, totalBytes, out embeddedImageRepeats));

            Stream imageStream = imageFormat.CreateWriter(imageOptions).CreateOutputStream(output, true, EncodingConfiguration.BufferSize);
            await WriteHeaderAsync(imageStream);
            await imageStream.WriteAsync(pixelStorageBytes, 0, pixelStorageBytes.Length);

            if (configuration.HasEmbeddedImage)
            {
                return new PixelStorageWriterStream(
                    imageStream,
                    new SubStream(new RepeatingStream(embeddedImageStream, embeddedImageRepeats), imageStream.Position, imageStream.Length - imageStream.Position),
                    pixelStorageOptions,
                    leaveOpen: false,
                    bufferSize: EncodingConfiguration.BufferSize);
            }

            return imageStream;
        }

        protected override async Task WriteBodyData(Stream imageStream, byte[] chunkLayoutBytes, ChunkLayoutBuilder chunkLayoutBuilder)
        {
            await imageStream.WriteAsync(chunkLayoutBytes, 0, chunkLayoutBytes.Length);

            await WriteChunkData(imageStream, chunkLayoutBuilder);

            var pixelStorageStream = imageStream as PixelStorageStream;

            await WritePaddingAsync(imageStream);
            // Important to pad the image directly as the channel bits may not line up correctly
            // with the image file when using a low amount of bits per channel.
            if (pixelStorageStream != null 
                && pixelStorageStream.ImageFormatterStream.Position < pixelStorageStream.ImageFormatterStream.Length)
            {
                await WritePaddingAsync(pixelStorageStream.ImageFormatterStream);
            }
        }
    }
}