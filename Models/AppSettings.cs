namespace BalanceDock.Models
{
    public sealed class AppSettings
    {
        public bool Topmost { get; set; }
        public bool RunAtStartup { get; set; }
        public double Left { get; set; }
        public double Top { get; set; }
        public bool HasWindowPosition { get; set; }

        public AppSettings()
        {
            Topmost = true;
            RunAtStartup = true;
        }
    }
}
