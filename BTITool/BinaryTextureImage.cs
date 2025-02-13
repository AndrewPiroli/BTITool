﻿using GameFormatReader.Common;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using Chadsoft.CTools.Image;

namespace BTITool
{
    /// <summary>
    /// The BinaryTextureImage (or BTI) format is used by Wind Waker (and several other Nintendo
    /// games) to store texture images.
    /// 
    /// Image data can be retrieved by calling GetData() which will return an ARGB array of bytes
    /// containing the information. For files without alpha data their values will be set to 0xFF.
    /// 
    /// BTI files are stored both individually on disk and embedded within other file formats. 
    /// </summary>
    public class BinaryTextureImage : INotifyPropertyChanged, IEquatable<BinaryTextureImage>
    {
        #region NotifyPropertyChanged overhead
        public event PropertyChangedEventHandler PropertyChanged;

        private void NotifyPropertyChanged([CallerMemberName] string propertyName = "")
        {
            if (PropertyChanged != null)
                PropertyChanged(this, new PropertyChangedEventArgs(propertyName));
        }
        #endregion

        #region Data Types
        /// <summary>
        /// ImageFormat specifies how the data within the image is encoded.
        /// Included is a chart of how many bits per pixel there are, 
        /// the width/height of each block, how many bytes long the
        /// actual block is, and a description of the type of data stored.
        /// </summary>
        public enum TextureFormats
        {
            //Bits per Pixel | Block Width | Block Height | Block Size | Type / Description
            I4 = 0x00,      // 4 | 8 | 8 | 32 | grey
            I8 = 0x01,      // 8 | 8 | 8 | 32 | grey
            IA4 = 0x02,     // 8 | 8 | 4 | 32 | grey + alpha
            IA8 = 0x03,     //16 | 4 | 4 | 32 | grey + alpha
            RGB565 = 0x04,  //16 | 4 | 4 | 32 | color
            RGB5A3 = 0x05,  //16 | 4 | 4 | 32 | color + alpha
            RGBA32 = 0x06,  //32 | 4 | 4 | 64 | color + alpha
            C4 = 0x08,      //4 | 8 | 8 | 32 | palette choices (IA8, RGB565, RGB5A3)
            C8 = 0x09,      //8, 8, 4, 32 | palette choices (IA8, RGB565, RGB5A3)
            C14X2 = 0x0a,   //16 (14 used) | 4 | 4 | 32 | palette (IA8, RGB565, RGB5A3)
            CMPR = 0x0e,    //4 | 8 | 8 | 32 | mini palettes in each block, RGB565 or transparent.

            PNG, // For output of PNG in BITTool
            TGA // For output of TGA in BITTool
        }

        /// <summary>
        /// Defines how textures handle going out of [0..1] range for texcoords.
        /// </summary>
        public enum WrapModes
        {
            ClampToEdge = 0,
            Repeat = 1,
            MirroredRepeat = 2,
        }

        /// <summary>
        /// PaletteFormat specifies how the data within the palette is stored. An
        /// image uses a single palette (except CMPR which defines its own
        /// mini-palettes within the Image data). Only C4, C8, and C14X2 use
        /// palettes. For all other formats the type and count is zero.
        /// </summary>
        public enum PaletteFormats
        {
            IA8 = 0x00,
            RGB565 = 0x01,
            RGB5A3 = 0x02,
        }

        /// <summary>
        /// FilterMode specifies what type of filtering the file should use for min/mag.
        /// </summary>
        public enum FilterMode
        {
            /* Valid in both Min and Mag Filter */
            Nearest = 0x0,                  // Point Sampling, No Mipmap
            Linear = 0x1,                   // Bilinear Filtering, No Mipmap

            /* Valid in only Min Filter */
            NearestMipmapNearest = 0x2,     // Point Sampling, Discrete Mipmap
            NearestMipmapLinear = 0x3,      // Bilinear Filtering, Discrete Mipmap
            LinearMipmapNearest = 0x4,      // Point Sampling, Linear MipMap
            LinearMipmapLinear = 0x5,       // Trilinear Filtering
        }

        /// <summary>
        /// The Palette simply stores the color data as loaded from the file.
        /// It does not convert the files based on the Palette type to RGBA8.
        /// </summary>
        public sealed class Palette
        {
            private byte[] _paletteData;

            public void Load(EndianBinaryReader reader, uint paletteEntryCount)
            {
                //Files that don't have palettes have an entry count of zero.
                if (paletteEntryCount == 0)
                {
                    _paletteData = new byte[0];
                    return;
                }

                //All palette formats are 2 bytes per entry.
                _paletteData = reader.ReadBytes((int)paletteEntryCount * 2);
            }

            public byte[] GetBytes()
            {
                return _paletteData;
            }
        }
        #endregion

        public string Name { get; private set; }
        public TextureFormats Format { get; private set; }
        public byte AlphaSetting { get; private set; } // 0 for no alpha, 0x02 and other values seem to indicate yes alpha.
        public ushort Width { get; private set; }
        public ushort Height { get; private set; }
        public WrapModes WrapS { get; private set; }
        public WrapModes WrapT { get; private set; }
        public PaletteFormats PaletteFormat { get; private set; }
        public ushort PaletteCount { get; private set; }
        public Color32 BorderColor { get; private set; } // This is a guess. It seems to be 0 in most things, but it fits with min/mag filters.
        public FilterMode MinFilter { get; private set; }
        public FilterMode MagFilter { get; private set; }
        public byte MinLOD { get; private set; } // Fixed point number, 1/8 = conversion (ToDo: is this multiply by 8 or divide...)
        public byte MagLOD { get; private set; } // Fixed point number, 1/8 = conversion (ToDo: is this multiply by 8 or divide...)
        public byte MipMapCount { get; private set; }
        public ushort LodBias { get; private set; } // Fixed point number, 1/100 = conversion

        public System.Windows.Media.Imaging.BitmapSource DisplaySource
        {
            get { return m_displaySource; }
            set
            {
                if (m_displaySource != value)
                {
                    m_displaySource = value;
                    NotifyPropertyChanged();
                }
            }
        }

        private System.Windows.Media.Imaging.BitmapSource m_displaySource;
        private Palette m_imagePalette;
        private byte[] m_rgbaImageData;

        public BinaryTextureImage()
        {

        }

        public BinaryTextureImage(string name, Bitmap bmp, TextureFormats format)
        {
            Name = name;
            Format = format;
            AlphaSetting = 0;
            Width = (ushort)bmp.Width;
            Height = (ushort)bmp.Height;
            WrapS = WrapModes.Repeat;
            WrapT = WrapModes.Repeat;
            PaletteFormat = PaletteFormats.IA8;
            PaletteCount = 0;
            BorderColor = new Color32(0, 0, 0, 0);
            MinFilter = FilterMode.Linear;
            MagFilter = FilterMode.Linear;
            MinLOD = 0;
            MagLOD = 0;
            MipMapCount = 1;
            LodBias = 0;

            double ratioX = (double)128 / (double)Height;
            double ratioY = (double)128 / (double)Width;

            double ratio = ratioX < ratioY ? ratioX : ratioY;

            Bitmap resizedOriginal = new Bitmap(Convert.ToInt32((ratio * Width)), Convert.ToInt32((ratio * Height)), PixelFormat.Format32bppArgb);
            Graphics g = Graphics.FromImage(resizedOriginal);
            g.DrawImage(bmp, 0, 0, Convert.ToInt32((ratio * Width)), Convert.ToInt32((ratio * Height)));
            g.Dispose();

            DisplaySource = ConvertToBmpSource(resizedOriginal);

            m_imagePalette = null;
            m_rgbaImageData = BinaryTextureImage.EncodeData(bmp, bmp.Width, bmp.Height, format);
        }

        private System.Windows.Media.Imaging.BitmapSource ConvertToBmpSource(Bitmap bmp)
        {
            var bitmapData = bmp.LockBits(
                new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height),
                System.Drawing.Imaging.ImageLockMode.ReadOnly, bmp.PixelFormat);

            var bitmapSource = System.Windows.Media.Imaging.BitmapSource.Create(
                bitmapData.Width, bitmapData.Height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32, null,
                bitmapData.Scan0, bitmapData.Stride * bitmapData.Height, bitmapData.Stride);

            bmp.UnlockBits(bitmapData);

            return bitmapSource;
        }

        // headerStart seems to be chunkStart + 0x20 and I don't know why.
        public void Load(EndianBinaryReader stream, string name, int headerStart, int imageIndex = 0)
        {
            Name = name;

            Format = (TextureFormats)stream.ReadByte();
            AlphaSetting = stream.ReadByte();
            Width = stream.ReadUInt16();
            Height = stream.ReadUInt16();
            WrapS = (WrapModes)stream.ReadByte();
            WrapT = (WrapModes)stream.ReadByte();
            byte unknown1 = stream.ReadByte();
            PaletteFormat = (PaletteFormats)stream.ReadByte();
            PaletteCount = stream.ReadUInt16();
            int paletteDataOffset = stream.ReadInt32();
            BorderColor = new Color32(stream.ReadByte(), stream.ReadByte(), stream.ReadByte(), stream.ReadByte());
            MinFilter = (FilterMode)stream.ReadByte();
            MagFilter = (FilterMode)stream.ReadByte();
            short unknown2 = stream.ReadInt16();
            MipMapCount = stream.ReadByte();
            byte unknown3 = stream.ReadByte();
            LodBias = stream.ReadUInt16();

            int imageDataOffset = stream.ReadInt32();

            // Load the Palette data 
            stream.BaseStream.Position = headerStart + paletteDataOffset + (0x20 * imageIndex);
            m_imagePalette = new Palette();
            m_imagePalette.Load(stream, PaletteCount);

            // Now load and decode image data into an ARGB array.
            stream.BaseStream.Position = headerStart + imageDataOffset + (0x20 * imageIndex);
            m_rgbaImageData = DecodeData(stream, Width, Height, Format, m_imagePalette, PaletteFormat);

            for (int i = 0; i < m_rgbaImageData.Length; i += 4)
            {
                byte alpha = m_rgbaImageData[i];
                byte red = m_rgbaImageData[i + 1];
                byte green = m_rgbaImageData[i + 2];
                byte blue = m_rgbaImageData[i + 3];

                m_rgbaImageData[i] = red;
                m_rgbaImageData[i + 1] = green;
                m_rgbaImageData[i + 2] = blue;
                m_rgbaImageData[i + 3] = alpha;
            }

            Bitmap sourceInput = GetBitmap(Width, Height, m_rgbaImageData);

            double ratioX = (double)128 / (double)Height;
            double ratioY = (double)128 / (double)Width;

            double ratio = ratioX < ratioY ? ratioX : ratioY;

            Bitmap resizedOriginal = new Bitmap(Convert.ToInt32((ratio * Width)), Convert.ToInt32((ratio * Height)), PixelFormat.Format32bppArgb);
            Graphics g = Graphics.FromImage(resizedOriginal);
            g.DrawImage(sourceInput, 0, 0, Convert.ToInt32((ratio * Width)), Convert.ToInt32((ratio * Height)));
            g.Dispose();

            DisplaySource = ConvertToBmpSource(resizedOriginal);
        }

        private Bitmap GetBitmap(ushort width, ushort height, byte[] data)
        {
            Bitmap bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            Rectangle rect = new Rectangle(0, 0, width, height);
            BitmapData bmpData = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);

            //Lock the bitmap for writing, copy the bits and then unlock for saving.
            IntPtr ptr = bmpData.Scan0;
            Marshal.Copy(data, 0, ptr, data.Length);
            bmp.UnlockBits(bmpData);

            return bmp;
        }

        public void SaveImageToDisk(string outputFile, byte[] imageData, int width, int height)
        {
            using (Bitmap bmp = new Bitmap(width, height))
            {
                Rectangle rect = new Rectangle(0, 0, width, height);
                BitmapData bmpData = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);

                //Lock the bitmap for writing, copy the bits and then unlock for saving.
                IntPtr ptr = bmpData.Scan0;
                Marshal.Copy(imageData, 0, ptr, imageData.Length);
                bmp.UnlockBits(bmpData);

                // Bitmaps will throw an exception if the output folder doesn't exist so...
                Directory.CreateDirectory(Path.GetDirectoryName(outputFile));
                bmp.Save(outputFile);
            }
        }

        public byte[] GetData()
        {
            return m_rgbaImageData;
        }

        public void WriteHeader(EndianBinaryWriter writer)
        {
            writer.Write((byte)Format);
            writer.Write(AlphaSetting);
            writer.Write(Width);
            writer.Write(Height);
            writer.Write((byte)WrapS);
            writer.Write((byte)WrapT);

            // This is an unknown
            writer.Write((byte)0);

            writer.Write((byte)PaletteFormat);
            writer.Write((short)PaletteCount);

            // This is a placeholder for PaletteDataOffset
            writer.Write((int)0);

            writer.Write((byte)BorderColor.R);
            writer.Write((byte)BorderColor.G);
            writer.Write((byte)BorderColor.B);
            writer.Write((byte)BorderColor.A);

            writer.Write((byte)MinFilter);
            writer.Write((byte)MagFilter);

            // This is an unknown
            writer.Write((short)0);

            writer.Write((byte)MipMapCount);

            // This is an unknown
            writer.Write((byte)0);

            writer.Write((short)LodBias);

            // This is a placeholder for ImageDataOffset
            writer.Write((int)0);
        }

        #region Decoding
        public static byte[] DecodeData(EndianBinaryReader stream, uint width, uint height, TextureFormats format, Palette imagePalette, PaletteFormats paletteFormat)
        {
            switch (format)
            {
                case TextureFormats.I4:
                    return DecodeI4(stream, width, height);
                case TextureFormats.I8:
                    return DecodeI8(stream, width, height);
                case TextureFormats.IA4:
                    return DecodeIA4(stream, width, height);
                case TextureFormats.IA8:
                    return DecodeIA8(stream, width, height);
                case TextureFormats.RGB565:
                    return DecodeRgb565(stream, width, height);
                case TextureFormats.RGB5A3:
                    return DecodeRgb5A3(stream, width, height);
                case TextureFormats.RGBA32:
                    return DecodeRgba32(stream, width, height);
                case TextureFormats.C4:
                    return DecodeC4(stream, width, height, imagePalette, paletteFormat);
                case TextureFormats.C8:
                    return DecodeC8(stream, width, height, imagePalette, paletteFormat);
                case TextureFormats.CMPR:
                    return DecodeCmpr(stream, width, height);
                case TextureFormats.C14X2:
                default:
                    //WLog.Warning(LogCategory.Textures, null, "Unsupported Binary Texture Image format {0}, unable to decode!", format);
                    return new byte[0];
            }
        }

        public static byte[] DecodeData(EndianBinaryReader stream, uint width, uint height, TextureFormats format)
        {
            switch (format)
            {
                case TextureFormats.I4:
                    return DecodeI4(stream, width, height);
                case TextureFormats.I8:
                    return DecodeI8(stream, width, height);
                case TextureFormats.IA4:
                    return DecodeIA4(stream, width, height);
                case TextureFormats.IA8:
                    return DecodeIA8(stream, width, height);
                case TextureFormats.RGB565:
                    return DecodeRgb565(stream, width, height);
                case TextureFormats.RGB5A3:
                    return DecodeRgb5A3(stream, width, height);
                case TextureFormats.RGBA32:
                    return DecodeRgba32(stream, width, height);
                case TextureFormats.C4:
                    //return DecodeC4(stream, width, height, imagePalette, paletteFormat);
                case TextureFormats.C8:
                    //return DecodeC8(stream, width, height, imagePalette, paletteFormat);
                case TextureFormats.CMPR:
                    return DecodeCmpr(stream, width, height);
                case TextureFormats.C14X2:
                default:
                    //WLog.Warning(LogCategory.Textures, null, "Unsupported Binary Texture Image format {0}, unable to decode!", format);
                    return new byte[0];
            }
        }

        private static byte[] DecodeRgba32(EndianBinaryReader stream, uint width, uint height)
        {
            uint numBlocksW = width / 4; //4 byte block width
            uint numBlocksH = height / 4; //4 byte block height 

            byte[] decodedData = new byte[width * height * 4];

            for (int yBlock = 0; yBlock < numBlocksH; yBlock++)
            {
                for (int xBlock = 0; xBlock < numBlocksW; xBlock++)
                {
                    //For each block, we're going to examine block width / block height number of 'pixels'
                    for (int pY = 0; pY < 4; pY++)
                    {
                        for (int pX = 0; pX < 4; pX++)
                        {
                            //Ensure the pixel we're checking is within bounds of the image.
                            if ((xBlock * 4 + pX >= width) || (yBlock * 4 + pY >= height))
                                continue;

                            //Now we're looping through each pixel in a block, but a pixel is four bytes long. 
                            uint destIndex = (uint)(4 * (width * ((yBlock * 4) + pY) + (xBlock * 4) + pX));
                            decodedData[destIndex + 3] = stream.ReadByte(); //Alpha
                            decodedData[destIndex + 2] = stream.ReadByte(); //Red
                        }
                    }

                    //...but we have to do it twice, because RGBA32 stores two sub-blocks per block. (AR, and GB)
                    for (int pY = 0; pY < 4; pY++)
                    {
                        for (int pX = 0; pX < 4; pX++)
                        {
                            //Ensure the pixel we're checking is within bounds of the image.
                            if ((xBlock * 4 + pX >= width) || (yBlock * 4 + pY >= height))
                                continue;

                            //Now we're looping through each pixel in a block, but a pixel is four bytes long. 
                            uint destIndex = (uint)(4 * (width * ((yBlock * 4) + pY) + (xBlock * 4) + pX));
                            decodedData[destIndex + 1] = stream.ReadByte(); //Green
                            decodedData[destIndex + 0] = stream.ReadByte(); //Blue
                        }
                    }

                }
            }

            return decodedData;
        }

        private static byte[] DecodeC4(EndianBinaryReader stream, uint width, uint height, Palette imagePalette, PaletteFormats paletteFormat)
        {
            //4 bpp, 8 block width/height, block size 32 bytes, possible palettes (IA8, RGB565, RGB5A3)
            uint numBlocksW = width / 8;
            uint numBlocksH = height / 8;

            byte[] decodedData = new byte[width * height * 8];

            //Read the indexes from the file
            for (int yBlock = 0; yBlock < numBlocksH; yBlock++)
            {
                for (int xBlock = 0; xBlock < numBlocksW; xBlock++)
                {
                    //Inner Loop for pixels
                    for (int pY = 0; pY < 8; pY++)
                    {
                        for (int pX = 0; pX < 8; pX += 2)
                        {
                            //Ensure we're not reading past the end of the image.
                            if ((xBlock * 8 + pX >= width) || (yBlock * 8 + pY >= height))
                                continue;

                            byte data = stream.ReadByte();
                            byte t = (byte)(data & 0xF0);
                            byte t2 = (byte)(data & 0x0F);

                            decodedData[width * ((yBlock * 8) + pY) + (xBlock * 8) + pX + 0] = (byte)(t >> 4);
                            decodedData[width * ((yBlock * 8) + pY) + (xBlock * 8) + pX + 1] = t2;
                        }
                    }
                }
            }

            //Now look them up in the palette and turn them into actual colors.
            byte[] finalDest = new byte[decodedData.Length / 2];

            int pixelSize = paletteFormat == PaletteFormats.IA8 ? 2 : 4;
            int destOffset = 0;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    UnpackPixelFromPalette(decodedData[y * width + x], ref finalDest, destOffset, imagePalette.GetBytes(), paletteFormat);
                    destOffset += pixelSize;
                }
            }

            return finalDest;
        }

        private static byte[] DecodeC8(EndianBinaryReader stream, uint width, uint height, Palette imagePalette, PaletteFormats paletteFormat)
        {
            //4 bpp, 8 block width/4 block height, block size 32 bytes, possible palettes (IA8, RGB565, RGB5A3)
            uint numBlocksW = width / 8;
            uint numBlocksH = height / 4;

            byte[] decodedData = new byte[width * height * 8];

            //Read the indexes from the file
            for (int yBlock = 0; yBlock < numBlocksH; yBlock++)
            {
                for (int xBlock = 0; xBlock < numBlocksW; xBlock++)
                {
                    //Inner Loop for pixels
                    for (int pY = 0; pY < 4; pY++)
                    {
                        for (int pX = 0; pX < 8; pX++)
                        {
                            //Ensure we're not reading past the end of the image.
                            if ((xBlock * 8 + pX >= width) || (yBlock * 4 + pY >= height))
                                continue;


                            byte data = stream.ReadByte();
                            decodedData[width * ((yBlock * 4) + pY) + (xBlock * 8) + pX] = data;
                        }
                    }
                }
            }

            //Now look them up in the palette and turn them into actual colors.
            byte[] finalDest = new byte[decodedData.Length / 2];

            int pixelSize = paletteFormat == PaletteFormats.IA8 ? 2 : 4;
            int destOffset = 0;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    UnpackPixelFromPalette(decodedData[y * width + x], ref finalDest, destOffset, imagePalette.GetBytes(), paletteFormat);
                    destOffset += pixelSize;
                }
            }

            return finalDest;
        }

        private static byte[] DecodeRgb565(EndianBinaryReader stream, uint width, uint height)
        {
            //16 bpp, 4 block width/height, block size 32 bytes, color.
            uint numBlocksW = width / 4;
            uint numBlocksH = height / 4;

            byte[] decodedData = new byte[width * height * 4];

            //Read the indexes from the file
            for (int yBlock = 0; yBlock < numBlocksH; yBlock++)
            {
                for (int xBlock = 0; xBlock < numBlocksW; xBlock++)
                {
                    //Inner Loop for pixels
                    for (int pY = 0; pY < 4; pY++)
                    {
                        for (int pX = 0; pX < 4; pX++)
                        {
                            //Ensure we're not reading past the end of the image.
                            if ((xBlock * 4 + pX >= width) || (yBlock * 4 + pY >= height))
                                continue;

                            ushort sourcePixel = stream.ReadUInt16();
                            RGB565ToRGBA8(sourcePixel, ref decodedData,
                                (int)(4 * (width * ((yBlock * 4) + pY) + (xBlock * 4) + pX)));
                        }
                    }
                }
            }

            return decodedData;
        }

        private static byte[] DecodeCmpr(EndianBinaryReader stream, uint width, uint height)
        {
            //Decode S3TC1
            byte[] buffer = new byte[width * height * 4];

            for (int y = 0; y < height / 4; y += 2)
            {
                for (int x = 0; x < width / 4; x += 2)
                {
                    for (int dy = 0; dy < 2; ++dy)
                    {
                        for (int dx = 0; dx < 2; ++dx)
                        {
                            if (4 * (x + dx) < width && 4 * (y + dy) < height)
                            {
                                byte[] fileData = stream.ReadBytes(8);
                                Buffer.BlockCopy(fileData, 0, buffer, (int)(8 * ((y + dy) * width / 4 + x + dx)), 8);
                            }
                        }
                    }
                }
            }

            for (int i = 0; i < width * height / 2; i += 8)
            {
                // Micro swap routine needed
                Swap(ref buffer[i], ref buffer[i + 1]);
                Swap(ref buffer[i + 2], ref buffer[i + 3]);

                buffer[i + 4] = S3TC1ReverseByte(buffer[i + 4]);
                buffer[i + 5] = S3TC1ReverseByte(buffer[i + 5]);
                buffer[i + 6] = S3TC1ReverseByte(buffer[i + 6]);
                buffer[i + 7] = S3TC1ReverseByte(buffer[i + 7]);
            }

            //Now decompress the DXT1 data within it.
            return DecompressDxt1(buffer, width, height);
        }

        private static void Swap(ref byte b1, ref byte b2)
        {
            byte tmp = b1; b1 = b2; b2 = tmp;
        }

        private static ushort Read16Swap(byte[] data, uint offset)
        {
            return (ushort)((Buffer.GetByte(data, (int)offset + 1) << 8) | Buffer.GetByte(data, (int)offset));
        }

        private static uint Read32Swap(byte[] data, uint offset)
        {
            return (uint)((Buffer.GetByte(data, (int)offset + 3) << 24) | (Buffer.GetByte(data, (int)offset + 2) << 16) | (Buffer.GetByte(data, (int)offset + 1) << 8) | Buffer.GetByte(data, (int)offset));
        }

        private static byte S3TC1ReverseByte(byte b)
        {
            byte b1 = (byte)(b & 0x3);
            byte b2 = (byte)(b & 0xC);
            byte b3 = (byte)(b & 0x30);
            byte b4 = (byte)(b & 0xC0);

            return (byte)((b1 << 6) | (b2 << 2) | (b3 >> 2) | (b4 >> 6));
        }

        private static byte[] DecompressDxt1(byte[] src, uint width, uint height)
        {
            uint dataOffset = 0;
            byte[] finalData = new byte[width * height * 4];

            for (int y = 0; y < height; y += 4)
            {
                for (int x = 0; x < width; x += 4)
                {
                    // Haha this is in little-endian (DXT1) so we have to swap the already swapped bytes.
                    ushort color1 = Read16Swap(src, dataOffset);
                    ushort color2 = Read16Swap(src, dataOffset + 2);
                    uint bits = Read32Swap(src, dataOffset + 4);
                    dataOffset += 8;

                    byte[][] ColorTable = new byte[4][];
                    for (int i = 0; i < 4; i++)
                        ColorTable[i] = new byte[4];

                    RGB565ToRGBA8(color1, ref ColorTable[0], 0);
                    RGB565ToRGBA8(color2, ref ColorTable[1], 0);

                    if (color1 > color2)
                    {
                        ColorTable[2][0] = (byte)((2 * ColorTable[0][0] + ColorTable[1][0] + 1) / 3);
                        ColorTable[2][1] = (byte)((2 * ColorTable[0][1] + ColorTable[1][1] + 1) / 3);
                        ColorTable[2][2] = (byte)((2 * ColorTable[0][2] + ColorTable[1][2] + 1) / 3);
                        ColorTable[2][3] = 0xFF;

                        ColorTable[3][0] = (byte)((ColorTable[0][0] + 2 * ColorTable[1][0] + 1) / 3);
                        ColorTable[3][1] = (byte)((ColorTable[0][1] + 2 * ColorTable[1][1] + 1) / 3);
                        ColorTable[3][2] = (byte)((ColorTable[0][2] + 2 * ColorTable[1][2] + 1) / 3);
                        ColorTable[3][3] = 0xFF;
                    }
                    else
                    {
                        ColorTable[2][0] = (byte)((ColorTable[0][0] + ColorTable[1][0] + 1) / 2);
                        ColorTable[2][1] = (byte)((ColorTable[0][1] + ColorTable[1][1] + 1) / 2);
                        ColorTable[2][2] = (byte)((ColorTable[0][2] + ColorTable[1][2] + 1) / 2);
                        ColorTable[2][3] = 0xFF;

                        ColorTable[3][0] = (byte)((ColorTable[0][0] + 2 * ColorTable[1][0] + 1) / 3);
                        ColorTable[3][1] = (byte)((ColorTable[0][1] + 2 * ColorTable[1][1] + 1) / 3);
                        ColorTable[3][2] = (byte)((ColorTable[0][2] + 2 * ColorTable[1][2] + 1) / 3);
                        ColorTable[3][3] = 0x00;
                    }

                    for (int iy = 0; iy < 4; ++iy)
                    {
                        for (int ix = 0; ix < 4; ++ix)
                        {
                            if (((x + ix) < width) && ((y + iy) < height))
                            {
                                int di = (int)(4 * ((y + iy) * width + x + ix));
                                int si = (int)(bits & 0x3);
                                finalData[di + 0] = ColorTable[si][0];
                                finalData[di + 1] = ColorTable[si][1];
                                finalData[di + 2] = ColorTable[si][2];
                                finalData[di + 3] = ColorTable[si][3];
                            }
                            bits >>= 2;
                        }
                    }
                }
            }

            return finalData;
        }

        private static byte[] DecodeIA8(EndianBinaryReader stream, uint width, uint height)
        {
            uint numBlocksW = width / 4; //4 byte block width
            uint numBlocksH = height / 4; //4 byte block height 

            byte[] decodedData = new byte[width * height * 4];

            for (int yBlock = 0; yBlock < numBlocksH; yBlock++)
            {
                for (int xBlock = 0; xBlock < numBlocksW; xBlock++)
                {
                    //For each block, we're going to examine block width / block height number of 'pixels'
                    for (int pY = 0; pY < 4; pY++)
                    {
                        for (int pX = 0; pX < 4; pX++)
                        {
                            //Ensure the pixel we're checking is within bounds of the image.
                            if ((xBlock * 4 + pX >= width) || (yBlock * 4 + pY >= height))
                                continue;

                            //Now we're looping through each pixel in a block, but a pixel is four bytes long. 
                            uint destIndex = (uint)(4 * (width * ((yBlock * 4) + pY) + (xBlock * 4) + pX));
                            byte byte0 = stream.ReadByte();
                            byte byte1 = stream.ReadByte();
                            decodedData[destIndex + 3] = byte0;
                            decodedData[destIndex + 2] = byte1;
                            decodedData[destIndex + 1] = byte1;
                            decodedData[destIndex + 0] = byte1;
                        }
                    }
                }
            }

            return decodedData;
        }

        private static byte[] DecodeIA4(EndianBinaryReader stream, uint width, uint height)
        {
            uint numBlocksW = width / 8;
            uint numBlocksH = height / 4;

            byte[] decodedData = new byte[width * height * 4];

            for (int yBlock = 0; yBlock < height; yBlock++)
            {
                for (int xBlock = 0; xBlock < width; xBlock++)
                {
                    //For each block, we're going to examine block width / block height number of 'pixels'
                    for (int pY = 0; pY < 4; pY++)
                    {
                        for (int pX = 0; pX < 8; pX++)
                        {
                            //Ensure the pixel we're checking is within bounds of the image.
                            if ((xBlock * 8 + pX >= width) || (yBlock * 4 + pY >= height))
                                continue;


                            byte value = stream.ReadByte();

                            byte alpha = (byte)((value & 0xF0) >> 4);
                            byte lum = (byte)(value & 0x0F);

                            uint destIndex = (uint)(4 * (width * ((yBlock * 4) + pY) + (xBlock * 8) + pX));

                            decodedData[destIndex + 0] = (byte)(lum * 0x11);
                            decodedData[destIndex + 1] = (byte)(lum * 0x11);
                            decodedData[destIndex + 2] = (byte)(lum * 0x11);
                            decodedData[destIndex + 3] = (byte)(alpha * 0x11);
                        }
                    }
                }
            }

            return decodedData;
        }

        private static byte[] DecodeI4(EndianBinaryReader stream, uint width, uint height)
        {
            uint numBlocksW = width / 8; //8 byte block width
            uint numBlocksH = height / 8; //8 byte block height 

            byte[] decodedData = new byte[width * height * 4];

            for (int yBlock = 0; yBlock < numBlocksH; yBlock++)
            {
                for (int xBlock = 0; xBlock < numBlocksW; xBlock++)
                {
                    //For each block, we're going to examine block width / block height number of 'pixels'
                    for (int pY = 0; pY < 8; pY++)
                    {
                        for (int pX = 0; pX < 8; pX += 2)
                        {
                            //Ensure the pixel we're checking is within bounds of the image.
                            if ((xBlock * 8 + pX >= width) || (yBlock * 8 + pY >= height))
                                continue;

                            byte data = stream.ReadByte();
                            byte t = (byte)((data & 0xF0) >> 4);
                            byte t2 = (byte)(data & 0x0F);
                            uint destIndex = (uint)(4 * (width * ((yBlock * 8) + pY) + (xBlock * 8) + pX));

                            decodedData[destIndex + 0] = (byte)(t * 0x11);
                            decodedData[destIndex + 1] = (byte)(t * 0x11);
                            decodedData[destIndex + 2] = (byte)(t * 0x11);
                            decodedData[destIndex + 3] = 0xFF;

                            decodedData[destIndex + 4] = (byte)(t2 * 0x11);
                            decodedData[destIndex + 5] = (byte)(t2 * 0x11);
                            decodedData[destIndex + 6] = (byte)(t2 * 0x11);
                            decodedData[destIndex + 7] = 0xFF;
                        }
                    }
                }
            }

            return decodedData;
        }

        private static byte[] DecodeI8(EndianBinaryReader stream, uint width, uint height)
        {
            uint numBlocksW = width / 8; //8 pixel block width
            uint numBlocksH = height / 4; //4 pixel block height 

            byte[] decodedData = new byte[width * height * 4];

            for (int yBlock = 0; yBlock < numBlocksH; yBlock++)
            {
                for (int xBlock = 0; xBlock < numBlocksW; xBlock++)
                {
                    //For each block, we're going to examine block width / block height number of 'pixels'
                    for (int pY = 0; pY < 4; pY++)
                    {
                        for (int pX = 0; pX < 8; pX++)
                        {
                            //Ensure the pixel we're checking is within bounds of the image.
                            if ((xBlock * 8 + pX >= width) || (yBlock * 4 + pY >= height))
                                continue;

                            byte data = stream.ReadByte();
                            uint destIndex = (uint)(4 * (width * ((yBlock * 4) + pY) + (xBlock * 8) + pX));

                            decodedData[destIndex + 0] = data;
                            decodedData[destIndex + 1] = data;
                            decodedData[destIndex + 2] = data;
                            decodedData[destIndex + 3] = 0xFF;
                        }
                    }
                }
            }

            return decodedData;
        }

        private static byte[] DecodeRgb5A3(EndianBinaryReader stream, uint width, uint height)
        {
            uint numBlocksW = width / 4; //4 byte block width
            uint numBlocksH = height / 4; //4 byte block height 

            byte[] decodedData = new byte[width * height * 4];

            for (int yBlock = 0; yBlock < numBlocksH; yBlock++)
            {
                for (int xBlock = 0; xBlock < numBlocksW; xBlock++)
                {
                    //For each block, we're going to examine block width / block height number of 'pixels'
                    for (int pY = 0; pY < 4; pY++)
                    {
                        for (int pX = 0; pX < 4; pX++)
                        {
                            //Ensure the pixel we're checking is within bounds of the image.
                            if ((xBlock * 4 + pX >= width) || (yBlock * 4 + pY >= height))
                                continue;

                            ushort sourcePixel = stream.ReadUInt16();
                            RGB5A3ToRGBA8(sourcePixel, ref decodedData,
                                (int)(4 * (width * ((yBlock * 4) + pY) + (xBlock * 4) + pX)));
                        }
                    }
                }
            }

            return decodedData;
        }

        private static void UnpackPixelFromPalette(int paletteIndex, ref byte[] dest, int offset, byte[] paletteData, PaletteFormats format)
        {
            switch (format)
            {
                case PaletteFormats.IA8:
                    dest[0] = paletteData[2 * paletteIndex + 1];
                    dest[1] = paletteData[2 * paletteIndex + 0];
                    break;
                case PaletteFormats.RGB565:
                    {
                        ushort palettePixelData = (ushort)((Buffer.GetByte(paletteData, 2 * paletteIndex) << 8) | Buffer.GetByte(paletteData, 2 * paletteIndex + 1));
                        RGB565ToRGBA8(palettePixelData, ref dest, offset);
                    }
                    break;
                case PaletteFormats.RGB5A3:
                    {
                        ushort palettePixelData = (ushort)((Buffer.GetByte(paletteData, 2 * paletteIndex) << 8) | Buffer.GetByte(paletteData, 2 * paletteIndex + 1));
                        RGB5A3ToRGBA8(palettePixelData, ref dest, offset);
                    }
                    break;
            }
        }



        /// <summary>
        /// Convert a RGB565 encoded pixel (two bytes in length) to a RGBA (4 byte in length)
        /// pixel.
        /// </summary>
        /// <param name="sourcePixel">RGB565 encoded pixel.</param>
        /// <param name="dest">Destination array for RGBA pixel.</param>
        /// <param name="destOffset">Offset into destination array to write RGBA pixel.</param>
        private static void RGB565ToRGBA8(ushort sourcePixel, ref byte[] dest, int destOffset)
        {
            byte r, g, b;
            r = (byte)((sourcePixel & 0xF100) >> 11);
            g = (byte)((sourcePixel & 0x7E0) >> 5);
            b = (byte)((sourcePixel & 0x1F));

            r = (byte)((r << (8 - 5)) | (r >> (10 - 8)));
            g = (byte)((g << (8 - 6)) | (g >> (12 - 8)));
            b = (byte)((b << (8 - 5)) | (b >> (10 - 8)));

            dest[destOffset] = b;
            dest[destOffset + 1] = g;
            dest[destOffset + 2] = r;
            dest[destOffset + 3] = 0xFF; //Set alpha to 1
        }

        /// <summary>
        /// Convert a RGB5A3 encoded pixel (two bytes in length) to an RGBA (4 byte in length)
        /// pixel.
        /// </summary>
        /// <param name="sourcePixel">RGB5A3 encoded pixel.</param>
        /// <param name="dest">Destination array for RGBA pixel.</param>
        /// <param name="destOffset">Offset into destination array to write RGBA pixel.</param>
        private static void RGB5A3ToRGBA8(ushort sourcePixel, ref byte[] dest, int destOffset)
        {
            byte r, g, b, a;

            //No alpha bits
            if ((sourcePixel & 0x8000) == 0x8000)
            {
                a = 0xFF;
                r = (byte)((sourcePixel & 0x7C00) >> 10);
                g = (byte)((sourcePixel & 0x3E0) >> 5);
                b = (byte)(sourcePixel & 0x1F);

                r = (byte)((r << (8 - 5)) | (r >> (10 - 8)));
                g = (byte)((g << (8 - 5)) | (g >> (10 - 8)));
                b = (byte)((b << (8 - 5)) | (b >> (10 - 8)));
            }
            //Alpha bits
            else
            {
                a = (byte)((sourcePixel & 0x7000) >> 12);
                r = (byte)((sourcePixel & 0xF00) >> 8);
                g = (byte)((sourcePixel & 0xF0) >> 4);
                b = (byte)(sourcePixel & 0xF);

                a = (byte)((a << (8 - 3)) | (a << (8 - 6)) | (a >> (9 - 8)));
                r = (byte)((r << (8 - 4)) | r);
                g = (byte)((g << (8 - 4)) | g);
                b = (byte)((b << (8 - 4)) | b);
            }

            dest[destOffset + 0] = a;
            dest[destOffset + 1] = b;
            dest[destOffset + 2] = g;
            dest[destOffset + 3] = r;
        }
        #endregion

        #region Encoding
        public static byte[] EncodeData(Bitmap bmp, int width, int height, TextureFormats format)
        {
            // Thanks, Chadderz from CToolsWii!
            byte[] imageData = GetData(bmp);

            switch (format)
            {
                case TextureFormats.I4:
                    return ImageDataFormat.I4.ConvertTo(imageData, width, height, null);
                case TextureFormats.RGB5A3:
                    return ImageDataFormat.RGB5A3.ConvertTo(imageData, width, height, null);
                case TextureFormats.RGBA32:
                    return ImageDataFormat.Rgba32.ConvertTo(imageData, width, height, null);
                case TextureFormats.CMPR:
                    return ImageDataFormat.Cmpr.ConvertTo(imageData, width, height, null);
                default:
                    return new byte[0];
            }
        }

        public static byte[] GetData(Bitmap bitmap)
        {
            BitmapData bitmapData;
            byte[] data;

            bitmapData = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            data = new byte[bitmapData.Height * bitmapData.Stride];

            Marshal.Copy(bitmapData.Scan0, data, 0, data.Length);

            bitmap.UnlockBits(bitmapData);

            return data;
        }

        /*
         * Keeping these for posterity
         * 
        private static byte[] EncodeI4(Bitmap bmp, uint width, uint height)
        {
            List<byte> encodedImage = new List<byte>();

            int blocksHCount = (int)height / 8;
            int blocksWCount = (int)width / 8;

            for (int yBlocks = 0; yBlocks < blocksHCount; yBlocks++)
            {
                for (int xBlocks = 0; xBlocks < blocksWCount; xBlocks++)
                {
                    for (int pY = 0; pY < 8; pY++)
                    {
                        byte curPixel = 0;
                        bool firstByteProcessed = false;

                        for (int pX = 0; pX < 8; pX++)
                        {
                            int srcXPixel = (xBlocks * 8) + pX;
                            int srcYPixel = (yBlocks * 8) + pY;

                            System.Drawing.Color pixelColor = bmp.GetPixel(srcXPixel, srcYPixel);
                            byte pixelIntensity = (byte)(pixelColor.R / 0x11);

                            // We haven't packed the first byte into the output byte, so this would be the left shifted
                            // first half.
                            if (!firstByteProcessed)
                            {
                                curPixel = (byte)(pixelIntensity << 4);
                                firstByteProcessed = true;
                            }
                            else
                            {
                                curPixel = (byte)(curPixel | pixelIntensity);

                                // curPixel now has two pixels into it, so we can pack it into the output stream.
                                encodedImage.Add(curPixel);
                                firstByteProcessed = false;
                            }
                        }
                    }
                }
            }

            return encodedImage.ToArray();
        }

        private static byte[] EncodeArgb32(Bitmap bmp, uint width, uint height)
        {
            List<byte> encodedImage = new List<byte>();

            uint numBlocksW = width / 4;
            uint numBlocksH = height / 4;

            for (int yBlock = 0; yBlock < numBlocksH; yBlock++)
            {
                for (int xBlock = 0; xBlock < numBlocksW; xBlock++)
                {
                    // Write BG data
                    for (int pY = 0; pY < 4; pY++)
                    {
                        for (int pX = 0; pX < 4; pX++)
                        {
                            int srcXPixel = (xBlock * 4) + pX;
                            int srcYPixel = (yBlock * 4) + pY;

                            System.Drawing.Color pixelColor = bmp.GetPixel(srcXPixel, srcYPixel);

                            encodedImage.Add(pixelColor.B);
                            encodedImage.Add(pixelColor.G);
                        }
                    }

                    // Write RA data
                    for (int pY = 0; pY < 4; pY++)
                    {
                        for (int pX = 0; pX < 4; pX++)
                        {
                            int srcXPixel = (xBlock * 4) + pX;
                            int srcYPixel = (yBlock * 4) + pY;

                            System.Drawing.Color pixelColor = bmp.GetPixel(srcXPixel, srcYPixel);

                            encodedImage.Add(pixelColor.A);
                            encodedImage.Add(pixelColor.R);
                        }
                    }
                }
            }

            return encodedImage.ToArray();
        }

        private static byte[] EncodeCMPR(Bitmap bmp, uint width, uint height)
        {
            return ImageDataFormat.Cmpr.ConvertTo(GetData(bmp), (int)width, (int)height, null);

            List<byte> encodedData = new List<byte>();

            // Y blocks of texture
            for (int y = 0; y < height; y += 4)
            {
                // X blocks of texture
                for (int x = 0; x < width; x += 4)
                {
                    ushort Color0; // Darkest
                    ushort Color1; // Brightest
                    ushort Color2; // Interpolated dark
                    ushort Color3; // Interpolated bright

                    int lowestColor = int.MaxValue;
                    int highestColor = int.MinValue;

                    // Get colors to find extremes
                    // Block Y
                    for (int blockY = 0; blockY < 4; blockY++)
                    {
                        // Block X pos
                        for (int blockX = 0; blockX < 4; blockX++)
                        {
                            int valueCompare = bmp.GetPixel(x + blockX, y + blockY).ToArgb();

                            if (valueCompare > highestColor)
                                highestColor = valueCompare;
                            if (valueCompare < lowestColor)
                                lowestColor = valueCompare;
                        }
                    }

                    System.Drawing.Color lowestAsColor = System.Drawing.Color.FromArgb(lowestColor);
                    System.Drawing.Color highestAsColor = System.Drawing.Color.FromArgb(highestColor);

                    Color0 = RGB8ToRGB565(lowestAsColor);
                    Color1 = RGB8ToRGB565(highestAsColor);

                    encodedData.AddRange(BitConverter.GetBytes(Color0));

                    // Swap endian of the short we just added
                    //byte endianSwapHold = encodedData[0];
                    //encodedData[0] = encodedData[1];
                    //encodedData[1] = endianSwapHold;

                    encodedData.AddRange(BitConverter.GetBytes(Color1));

                    // Swap endian of the short we just added
                    //endianSwapHold = encodedData[2];
                    //encodedData[2] = encodedData[3];
                    //encodedData[3] = endianSwapHold;

                    int col2R = (2 * lowestAsColor.R + highestAsColor.R) / 3;
                    int col2G = (2 * lowestAsColor.G + highestAsColor.G) / 3;
                    int col2B = (2 * lowestAsColor.B + highestAsColor.B) / 3;

                    int col3R = (lowestAsColor.R + 2 * highestAsColor.R) / 3;
                    int col3G = (lowestAsColor.G + 2 * highestAsColor.G) / 3;
                    int col3B = (lowestAsColor.B + 2 * highestAsColor.B) / 3;

                    Color2 = RGB8ToRGB565(System.Drawing.Color.FromArgb(col2R, col2G, col2B));
                    Color3 = RGB8ToRGB565(System.Drawing.Color.FromArgb(col3R, col3G, col3B));

                    ushort color0Midpoint = (ushort)((Color0 + Color2) / 2);
                    ushort color1Midpoint = (ushort)((Color1 + Color3) / 2);
                    ushort color23Midpoint = (ushort)((Color2 + Color3) / 2);

                    int packedPixels = 0;
                    int shiftVal = 0;

                    // Assign pixels to colors
                    // Block Y
                    for (int blockY = 0; blockY < 4; ++blockY)
                    {
                        // Block X pos
                        for (int blockX = 0; blockX < 4; ++blockX)
                        {
                            ushort colorVal = RGB8ToRGB565(bmp.GetPixel(x + blockX, y + blockY));

                            if ((colorVal >= Color0) && (colorVal < color0Midpoint))
                            {
                                packedPixels = packedPixels | (0 << shiftVal);
                            }

                            if ((colorVal <= Color1) && (colorVal > color1Midpoint))
                            {
                                packedPixels = packedPixels | (1 << shiftVal);
                            }

                            if ((colorVal >= color0Midpoint) && (colorVal < Color2))
                            {
                                packedPixels = packedPixels | (2 << shiftVal);
                            }

                            if ((colorVal >= Color2) && (colorVal < color23Midpoint))
                            {
                                packedPixels = packedPixels | (2 << shiftVal);
                            }

                            if ((colorVal >= color23Midpoint) && (colorVal < Color3))
                            {
                                packedPixels = packedPixels | (3 << shiftVal);
                            }

                            if ((colorVal >= Color3) && (colorVal < color1Midpoint))
                            {
                                packedPixels = packedPixels | (3 << shiftVal);
                            }
                            else
                            {
                            }

                            shiftVal += 2;
                        }
                    }

                    encodedData.AddRange(BitConverter.GetBytes(packedPixels));
                }
            }
        }

        private static ushort RGB8ToRGB565(System.Drawing.Color color)
        {
            return (ushort)((color.R >> 3) << 11 | (color.G >> 2) << 5 | (color.B >> 3));
        }
        */
        #endregion

        public override bool Equals(object obj)
        {
            if (obj.GetType() == typeof(BinaryTextureImage))
                return Compare((BinaryTextureImage)obj);
            else return false;
        }

        private bool Compare(BinaryTextureImage obj)
        {
            if (m_rgbaImageData == obj.m_rgbaImageData)
                return true;
            else
                return false;
        }

        public bool Equals(BinaryTextureImage other)
        {
            return other != null &&
                   Name == other.Name &&
                   Format == other.Format &&
                   AlphaSetting == other.AlphaSetting &&
                   Width == other.Width &&
                   Height == other.Height &&
                   WrapS == other.WrapS &&
                   WrapT == other.WrapT &&
                   PaletteFormat == other.PaletteFormat &&
                   PaletteCount == other.PaletteCount &&
                   EqualityComparer<Color32>.Default.Equals(BorderColor, other.BorderColor) &&
                   MinFilter == other.MinFilter &&
                   MagFilter == other.MagFilter &&
                   MinLOD == other.MinLOD &&
                   MagLOD == other.MagLOD &&
                   MipMapCount == other.MipMapCount &&
                   LodBias == other.LodBias &&
                   EqualityComparer<System.Windows.Media.Imaging.BitmapSource>.Default.Equals(DisplaySource, other.DisplaySource) &&
                   EqualityComparer<System.Windows.Media.Imaging.BitmapSource>.Default.Equals(m_displaySource, other.m_displaySource) &&
                   EqualityComparer<Palette>.Default.Equals(m_imagePalette, other.m_imagePalette) &&
                   EqualityComparer<byte[]>.Default.Equals(m_rgbaImageData, other.m_rgbaImageData);
        }

        public override int GetHashCode()
        {
            int hashCode = 1897818637;
            hashCode = hashCode * -1521134295 + EqualityComparer<string>.Default.GetHashCode(Name);
            hashCode = hashCode * -1521134295 + Format.GetHashCode();
            hashCode = hashCode * -1521134295 + AlphaSetting.GetHashCode();
            hashCode = hashCode * -1521134295 + Width.GetHashCode();
            hashCode = hashCode * -1521134295 + Height.GetHashCode();
            hashCode = hashCode * -1521134295 + WrapS.GetHashCode();
            hashCode = hashCode * -1521134295 + WrapT.GetHashCode();
            hashCode = hashCode * -1521134295 + PaletteFormat.GetHashCode();
            hashCode = hashCode * -1521134295 + PaletteCount.GetHashCode();
            hashCode = hashCode * -1521134295 + BorderColor.GetHashCode();
            hashCode = hashCode * -1521134295 + MinFilter.GetHashCode();
            hashCode = hashCode * -1521134295 + MagFilter.GetHashCode();
            hashCode = hashCode * -1521134295 + MinLOD.GetHashCode();
            hashCode = hashCode * -1521134295 + MagLOD.GetHashCode();
            hashCode = hashCode * -1521134295 + MipMapCount.GetHashCode();
            hashCode = hashCode * -1521134295 + LodBias.GetHashCode();
            hashCode = hashCode * -1521134295 + EqualityComparer<System.Windows.Media.Imaging.BitmapSource>.Default.GetHashCode(DisplaySource);
            hashCode = hashCode * -1521134295 + EqualityComparer<System.Windows.Media.Imaging.BitmapSource>.Default.GetHashCode(m_displaySource);
            hashCode = hashCode * -1521134295 + EqualityComparer<Palette>.Default.GetHashCode(m_imagePalette);
            hashCode = hashCode * -1521134295 + EqualityComparer<byte[]>.Default.GetHashCode(m_rgbaImageData);
            return hashCode;
        }

        public static bool operator ==(BinaryTextureImage left, BinaryTextureImage right)
        {
            if (System.Object.ReferenceEquals(left, right))
                return true;

            if (((object)left == null) || ((object)right == null))
                return false;

            if (left.m_rgbaImageData == right.m_rgbaImageData)
                return true;
            else
                return false;
        }

        public static bool operator !=(BinaryTextureImage left, BinaryTextureImage right)
        {
            if (System.Object.ReferenceEquals(left, right))
                return false;

            if (((object)left == null) || ((object)right == null))
                return true;

            if (left.m_rgbaImageData == right.m_rgbaImageData)
                return false;
            else
                return true;
        }
    }

    public struct Color32
    {
        public byte R, G, B, A;

        public Color32(byte r, byte g, byte b, byte a)
        {
            R = r;
            G = g;
            B = b;
            A = a;
        }

        public override string ToString()
        {
            return string.Format("[Color32] (r: {0} g: {1} b: {2} a: {3})", R, G, B, A);
        }

        public byte this[int index]
        {
            get
            {
                switch (index)
                {
                    case 0:
                        return R;
                    case 1:
                        return G;
                    case 2:
                        return B;
                    case 3:
                        return A;

                    default:
                        throw new ArgumentOutOfRangeException("index");
                }
            }
            set
            {
                switch (index)
                {
                    case 0:
                        R = value;
                        break;

                    case 1:
                        G = value;
                        break;

                    case 2:
                        B = value;
                        break;

                    case 3:
                        A = value;
                        break;

                    default:
                        throw new ArgumentOutOfRangeException("index");
                }
            }
        }
    }
}
