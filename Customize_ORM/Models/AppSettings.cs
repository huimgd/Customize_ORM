using Microsoft.Extensions.DependencyInjection;
using static NpgsqlTypes.NpgsqlTsQuery;

namespace Customize_ORM.Models
{
    public class AppSettings
    {
        private static bool _inited = false;
        public static AppSettings Current { get; private set; } = new();
        public IServiceProvider? ServiceProvider { get; private set; }
        public FileSettings FileSettings { get; set; } = new();
        public static void Initialize(AppSettings settings, IServiceProvider serviceProvider)
        {
            if (!_inited)
            {
                settings.ServiceProvider = serviceProvider;
                Current = settings;
                _inited = true;
            }
            else
            {
                throw new InvalidOperationException("Already Initialized");
            }
        }
    }
}
