// MultipleFiles/Utils/ImageProcessingUtils.cs
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace P5S_ceviri
{
    public static class ImageProcessingUtils
    {
        public static Bitmap ToGrayscale(Bitmap original)
        {
            if (original == null) return null;

            Bitmap grayscaleBitmap = new Bitmap(original.Width, original.Height, PixelFormat.Format8bppIndexed);

            ColorPalette palette = grayscaleBitmap.Palette;
            for (int i = 0; i < 256; i++)
            {
                palette.Entries[i] = Color.FromArgb(i, i, i);
            }
            grayscaleBitmap.Palette = palette;

            BitmapData originalData = null;
            BitmapData grayscaleData = null;

            try
            {
                originalData = original.LockBits(
                    new Rectangle(0, 0, original.Width, original.Height),
                    ImageLockMode.ReadOnly,
                    original.PixelFormat);

                grayscaleData = grayscaleBitmap.LockBits(
                    new Rectangle(0, 0, grayscaleBitmap.Width, grayscaleBitmap.Height),
                    ImageLockMode.WriteOnly,
                    grayscaleBitmap.PixelFormat);

                int originalStride = originalData.Stride;
                int grayscaleStride = grayscaleData.Stride;

                byte[] originalPixels = new byte[originalStride * original.Height];
                byte[] grayscalePixels = new byte[grayscaleStride * grayscaleBitmap.Height];

                Marshal.Copy(originalData.Scan0, originalPixels, 0, originalPixels.Length);

                int bytesPerPixel = Image.GetPixelFormatSize(original.PixelFormat) / 8;

                for (int y = 0; y < original.Height; y++)
                {
                    for (int x = 0; x < original.Width; x++)
                    {
                        int originalOffset = y * originalStride + x * bytesPerPixel;
                        int grayscaleOffset = y * grayscaleStride + x;

                        byte blue = originalPixels[originalOffset];
                        byte green = originalPixels[originalOffset + 1];
                        byte red = originalPixels[originalOffset + 2];

                        byte gray = (byte)((red * 0.299) + (green * 0.587) + (blue * 0.114));
                        grayscalePixels[grayscaleOffset] = gray;
                    }
                }

                Marshal.Copy(grayscalePixels, 0, grayscaleData.Scan0, grayscalePixels.Length);
            }
            finally
            {
                if (originalData != null) original.UnlockBits(originalData);
                if (grayscaleData != null) grayscaleBitmap.UnlockBits(grayscaleData);
            }

            return grayscaleBitmap;
        }

        public static Bitmap ToBinary(Bitmap grayscaleImage, byte threshold)
        {
            if (grayscaleImage == null || grayscaleImage.PixelFormat != PixelFormat.Format8bppIndexed)
            {
                throw new ArgumentException("Görüntü gri tonlamalı (8bppIndexed) olmalıdır.");
            }

            Bitmap binaryBitmap = new Bitmap(grayscaleImage.Width, grayscaleImage.Height, PixelFormat.Format1bppIndexed);

            ColorPalette palette = binaryBitmap.Palette;
            palette.Entries[0] = Color.Black;
            palette.Entries[1] = Color.White;
            binaryBitmap.Palette = palette;

            BitmapData grayscaleData = null;
            BitmapData binaryData = null;

            try
            {
                grayscaleData = grayscaleImage.LockBits(
                    new Rectangle(0, 0, grayscaleImage.Width, grayscaleImage.Height),
                    ImageLockMode.ReadOnly,
                    grayscaleImage.PixelFormat);

                binaryData = binaryBitmap.LockBits(
                    new Rectangle(0, 0, binaryBitmap.Width, binaryBitmap.Height),
                    ImageLockMode.WriteOnly,
                    binaryBitmap.PixelFormat);

                int grayscaleStride = grayscaleData.Stride;
                int binaryStride = binaryData.Stride;

                byte[] grayscalePixels = new byte[grayscaleStride * grayscaleImage.Height];
                byte[] binaryPixels = new byte[binaryStride * binaryBitmap.Height];

                Marshal.Copy(grayscaleData.Scan0, grayscalePixels, 0, grayscalePixels.Length);

                for (int y = 0; y < grayscaleImage.Height; y++)
                {
                    for (int x = 0; x < grayscaleImage.Width; x++)
                    {
                        int grayscaleOffset = y * grayscaleStride + x;
                        byte pixelValue = grayscalePixels[grayscaleOffset];

                        if (pixelValue < threshold)
                        {
                            binaryPixels[y * binaryStride + (x / 8)] |= (byte)(0x80 >> (x % 8));
                        }
                    }
                }

                Marshal.Copy(binaryPixels, 0, binaryData.Scan0, binaryPixels.Length);
            }
            finally
            {
                if (grayscaleData != null) grayscaleImage.UnlockBits(grayscaleData);
                if (binaryData != null) binaryBitmap.UnlockBits(binaryData);
            }

            return binaryBitmap;
        }

        public static List<Rectangle> FindConnectedComponents(Bitmap binaryImage)
        {
            if (binaryImage == null || binaryImage.PixelFormat != PixelFormat.Format1bppIndexed)
            {
                return new List<Rectangle>();
            }

            List<Rectangle> components = new List<Rectangle>();
            int width = binaryImage.Width;
            int height = binaryImage.Height;

            bool[,] visited = new bool[width, height];

            BitmapData imageData = null;
            byte[] pixels = null;
            int stride = 0;

            try
            {
                imageData = binaryImage.LockBits(
                    new Rectangle(0, 0, width, height),
                    ImageLockMode.ReadOnly,
                    binaryImage.PixelFormat);

                stride = imageData.Stride;
                pixels = new byte[stride * height];
                Marshal.Copy(imageData.Scan0, pixels, 0, pixels.Length);

                Func<int, int, bool> IsBlack = (x, y) =>
                {
                    if (x < 0 || x >= width || y < 0 || y >= height) return false;
                    int byteIndex = y * stride + (x / 8);
                    byte mask = (byte)(0x80 >> (x % 8));
                    return (pixels[byteIndex] & mask) != 0;
                };

                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        if (IsBlack(x, y) && !visited[x, y])
                        {
                            Queue<Point> queue = new Queue<Point>();
                            queue.Enqueue(new Point(x, y));
                            visited[x, y] = true;

                            int minX = x, maxX = x, minY = y, maxY = y;

                            while (queue.Count > 0)
                            {
                                Point current = queue.Dequeue();

                                minX = Math.Min(minX, current.X);
                                maxX = Math.Max(maxX, current.X);
                                minY = Math.Min(minY, current.Y);
                                maxY = Math.Max(maxY, current.Y);

                                for (int dy = -1; dy <= 1; dy++)
                                {
                                    for (int dx = -1; dx <= 1; dx++)
                                    {
                                        if (dx == 0 && dy == 0) continue;

                                        int nx = current.X + dx;
                                        int ny = current.Y + dy;

                                        if (nx >= 0 && nx < width && ny >= 0 && ny < height &&
                                            IsBlack(nx, ny) && !visited[nx, ny])
                                        {
                                            visited[nx, ny] = true;
                                            queue.Enqueue(new Point(nx, ny));
                                        }
                                    }
                                }
                            }

                            int componentWidth = maxX - minX + 1;
                            int componentHeight = maxY - minY + 1;

                            if (componentWidth > 5 && componentHeight > 5 &&
                                componentWidth < width / 2 && componentHeight < height / 2)
                            {
                                components.Add(new Rectangle(minX, minY, componentWidth, componentHeight));
                            }
                        }
                    }
                }
            }
            finally
            {
                if (imageData != null) binaryImage.UnlockBits(imageData);
            }

            return components;
        }
    }
}
