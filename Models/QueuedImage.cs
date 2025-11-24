using System;
using System.Windows.Media.Imaging;

namespace VPM.Models
{
    /// <summary>
    /// Represents an image queued for loading
    /// </summary>
    public class QueuedImage
    {
        public string VarPath { get; set; }
        public string InternalPath { get; set; }
        public bool IsThumbnail { get; set; }
        public int DecodeWidth { get; set; }
        public int DecodeHeight { get; set; }
        public Action<QueuedImage> Callback { get; set; }
        public byte[] RawData { get; set; }
        public System.IO.Stream RawDataStream { get; set; }
        public BitmapImage Texture { get; set; }
        public bool Processed { get; set; }
        public bool Finished { get; set; }
        public bool HadError { get; set; }
        public string ErrorText { get; set; }
        
        // Cache validation properties
        public long FileSize { get; set; }
        public long LastWriteTicks { get; set; }
    }
}
