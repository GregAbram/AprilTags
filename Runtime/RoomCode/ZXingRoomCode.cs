using System;
using System.Collections.Generic;
using AprilTag.Interop;
using UnityEngine;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;

// Room codes with ZXing.Net: reads QR codes from the localizer's grayscale
// frames (worker thread) and draws them as textures. Installed at startup as
// the package's room-code decoder and encoder.
public static class ZXingRoomCode
{
    // Runs at startup; apps may also call it explicitly. Safe to call again.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    public static void Install()
    {
        if (AprilTagRoomLocalizer.DefaultRoomCodeDecoder != null && RoomCodeImage.Encoder != null)
        {
            return;
        }
        try
        {
            AprilTagRoomLocalizer.DefaultRoomCodeDecoder ??= new Decoder();
            RoomCodeImage.Encoder ??= new Encoder();
            Debug.Log("[ZXingRoomCode] QR room-code decoder and encoder installed");
        }
        catch (Exception e)
        {
            Debug.LogError($"[ZXingRoomCode] Couldn't install the QR decoder: {e}");
        }
    }

    private sealed class Decoder : IRoomCodeDecoder
    {
        private readonly QRCodeReader reader = new();
        private readonly Dictionary<DecodeHintType, object> hints = new()
        {
            { DecodeHintType.TRY_HARDER, true },
            { DecodeHintType.POSSIBLE_FORMATS, new List<BarcodeFormat> { BarcodeFormat.QR_CODE } },
        };
        private byte[] pixels;

        public string Decode(ImageU8 image)
        {
            // Copy the rows without their stride padding.
            var width = image.Width;
            var height = image.Height;
            var stride = image.Stride;
            if (pixels == null || pixels.Length != width * height)
            {
                pixels = new byte[width * height];
            }
            var source = image.Buffer;
            for (var y = 0; y < height; y++)
            {
                source.Slice(y * stride, width).CopyTo(new Span<byte>(pixels, y * width, width));
            }

            var luminance = new RGBLuminanceSource(pixels, width, height, RGBLuminanceSource.BitmapFormat.Gray8);
            var result = reader.decode(new BinaryBitmap(new HybridBinarizer(luminance)), hints);
            reader.reset();
            return result?.Text;
        }
    }

    private sealed class Encoder : IRoomCodeEncoder
    {
        public Texture2D Render(string text, int pixels)
        {
            var matrix = new QRCodeWriter().encode(text, BarcodeFormat.QR_CODE, pixels, pixels,
                new Dictionary<EncodeHintType, object>
                {
                    { EncodeHintType.ERROR_CORRECTION, ZXing.QrCode.Internal.ErrorCorrectionLevel.M },
                    { EncodeHintType.MARGIN, 4 },
                    { EncodeHintType.CHARACTER_SET, "UTF-8" },
                });
            var texture = new Texture2D(matrix.Width, matrix.Height, TextureFormat.RGB24, false) { filterMode = FilterMode.Point };
            var colors = new Color32[matrix.Width * matrix.Height];
            var black = new Color32(0, 0, 0, 255);
            var white = new Color32(255, 255, 255, 255);
            for (var y = 0; y < matrix.Height; y++)
            {
                // Texture rows go bottom-up; the code's top row goes last, so the image isn't mirrored.
                var row = (matrix.Height - 1 - y) * matrix.Width;
                for (var x = 0; x < matrix.Width; x++)
                {
                    colors[row + x] = matrix[x, y] ? black : white;
                }
            }
            texture.SetPixels32(colors);
            texture.Apply();
            return texture;
        }
    }
}
