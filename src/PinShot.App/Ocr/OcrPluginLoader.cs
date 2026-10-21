using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using PinShot.Ocr.Abstractions;

namespace PinShot.App.Ocr;

/// <summary>
/// 加载可选的 OCR 插件（ocr-plugin 目录）。
/// 主程序不引用 WinRT 投影，插件缺失时 OCR 功能优雅降级。
/// </summary>
internal static class OcrPluginLoader
{
    private static readonly object Sync = new();

    private static IOcrEngine? _engine;
    private static bool _loaded;
    private static string _error = "未检测";

    /// <summary>插件目录：程序目录下的 ocr-plugin\。</summary>
    public static string PluginDirectory => Path.Combine(AppContext.BaseDirectory, "ocr-plugin");

    /// <summary>备用插件目录：单文件分发时插件被释放到 %LOCALAPPDATA%\PinShot\ocr-plugin。</summary>
    private static string FallbackDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PinShot", "ocr-plugin");

    /// <summary>已加载的引擎；未安装插件时为 null。</summary>
    public static IOcrEngine? Engine
    {
        get
        {
            if (_loaded)
            {
                return _engine;
            }

            lock (Sync)
            {
                if (!_loaded)
                {
                    (_engine, _error) = Load();
                    _loaded = true;
                }
            }

            return _engine;
        }
    }

    /// <summary>加载失败的原因，用于界面提示。</summary>
    public static string Error
    {
        get
        {
            _ = Engine;
            return _error;
        }
    }

    private static (IOcrEngine? Engine, string Error) Load()
    {
        try
        {
            var directory = ResolvePluginDirectory();
            if (directory is null)
            {
                return (null, "未安装 OCR 插件（缺少 ocr-plugin 目录）");
            }

            var dll = Directory.GetFiles(directory, "PinShot.Ocr.WinRt.dll",
                SearchOption.TopDirectoryOnly).FirstOrDefault();

            if (dll is null)
            {
                return (null, "未安装 OCR 插件（ocr-plugin 目录里没有 PinShot.Ocr.WinRt.dll）");
            }

            var context = new PluginContext(dll);
            var assembly = context.LoadFromAssemblyPath(dll);

            var type = assembly.GetTypes().FirstOrDefault(candidate =>
                typeof(IOcrEngine).IsAssignableFrom(candidate) &&
                candidate is { IsAbstract: false, IsInterface: false });

            if (type is null)
            {
                return (null, "OCR 插件里没有找到 IOcrEngine 实现");
            }

            if (Activator.CreateInstance(type) is not IOcrEngine engine)
            {
                return (null, "OCR 插件实例创建失败");
            }

            return (engine, string.Empty);
        }
        catch (Exception ex)
        {
            return (null, $"OCR 插件加载失败：{ex.Message}");
        }
    }

    /// <summary>先找程序目录，再找单文件分发时释放的 LocalAppData 目录。</summary>
    private static string? ResolvePluginDirectory()
    {
        var candidates = new[] { PluginDirectory, FallbackDirectory };

        foreach (var directory in candidates)
        {
            if (Directory.Exists(directory) &&
                Directory.GetFiles(directory, "PinShot.Ocr.WinRt.dll", SearchOption.TopDirectoryOnly)
                    .Length > 0)
            {
                return directory;
            }
        }

        return null;
    }

    /// <summary>插件独立加载上下文：优先用插件目录里的依赖，共享契约走主程序。</summary>
    private sealed class PluginContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;

        public PluginContext(string pluginPath)
            : base("PinShot.Ocr.Plugin")
        {
            _resolver = new AssemblyDependencyResolver(pluginPath);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // 契约程序集必须复用主程序那份，否则接口类型不匹配
            if (string.Equals(assemblyName.Name, "PinShot.Ocr.Abstractions",
                    StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path is null ? null : LoadFromAssemblyPath(path);
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
        }
    }
}
