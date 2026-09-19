// The linked ChatService is tested without loading Visual Studio or its package singleton.
namespace ApexCode
{
    internal static class ApexCodePackage
    {
        public static System.IServiceProvider SystemServiceProvider => null;
        public static System.Threading.Tasks.Task SaveActiveSessionAsync(string sessionId) =>
            System.Threading.Tasks.Task.CompletedTask;
    }
}
