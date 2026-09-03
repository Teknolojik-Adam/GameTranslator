namespace GameTranslatorUltimate
{
    public sealed class GameRecipe
    {
        public string ProcessName { get; set; }

        public PathInfo PathInfo { get; set; }

        public GameRecipe()
        {
            ProcessName = string.Empty;
        }
    }
}