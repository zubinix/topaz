namespace Topaz.Extra
{
    public static class Utils
    {
        private static string environ = null;

        public static string GetSelectedEnvironment()
        {
            return "UAT";
        }

        public static void SetSelectedEnvironment(string env)
        {
            environ = env;
        }
    }
}
