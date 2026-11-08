internal static class Features
{
#if PRIVATE || FANGBUCH
    public static readonly bool Log = true;
#else
    public static readonly bool Log = false;
#endif
#if PRIVATE
    public static readonly bool Tracker = true;
#else
    public static readonly bool Tracker = false;
#endif
#if PRIVATE || OVERLAY
    public static readonly bool Overlay = true;
#else
    public static readonly bool Overlay = false;
#endif
    public static readonly bool BaitHistory = true;
}
