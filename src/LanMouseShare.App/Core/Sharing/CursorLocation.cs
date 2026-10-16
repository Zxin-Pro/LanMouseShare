namespace LanMouseShare.Core.Sharing
{
    /// <summary>光标当前被哪台机器接管。</summary>
    public enum CursorLocation
    {
        /// <summary>在本机（主电脑）上。</summary>
        Local = 0,

        /// <summary>在副电脑上。</summary>
        Remote = 1
    }
}
