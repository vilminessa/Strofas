using System.Text.Json;
using Strofas.Models;

namespace Strofas.Core;

/// <summary>Загрузка/сохранение menu.json в %LOCALAPPDATA%\Strofas\.</summary>
internal static class ConfigService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string Dir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Strofas");

    public static string ConfigPath => Path.Combine(Dir, "menu.json");

    public static MenuConfig Load()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            if (!File.Exists(ConfigPath))
            {
                var def = CreateDefault();
                Save(def);
                Log.Info($"Создан конфиг по умолчанию: {ConfigPath}");
                return def;
            }

            var json = File.ReadAllText(ConfigPath);
            var cfg = JsonSerializer.Deserialize<MenuConfig>(json, Options);
            if (cfg is null) throw new InvalidOperationException("Пустой конфиг");
            Normalize(cfg);
            return cfg;
        }
        catch (Exception ex)
        {
            Log.Error("Ошибка чтения конфига, создаётся конфиг по умолчанию", ex);
            try
            {
                if (File.Exists(ConfigPath))
                    File.Copy(ConfigPath, ConfigPath + ".bad", overwrite: true);
            }
            catch
            {
                // ignore
            }
            var def = CreateDefault();
            try { Save(def); } catch { /* ignore */ }
            return def;
        }
    }

    public static void Save(MenuConfig cfg)
    {
        Directory.CreateDirectory(Dir);
        var json = JsonSerializer.Serialize(cfg, Options);
        File.WriteAllText(ConfigPath, json);
        Log.Info($"Конфиг сохранён: {ConfigPath}");
    }

    /// <summary>Глубокая копия — чтобы редактор не мутировал конфиг меню.</summary>
    public static MenuConfig Clone(MenuConfig cfg)
        => JsonSerializer.Deserialize<MenuConfig>(JsonSerializer.Serialize(cfg, Options), Options)!;

    private static void Normalize(MenuConfig cfg)
    {
        if (cfg.MenuSize is < 400 or > 1200) cfg.MenuSize = 640;
        cfg.Items ??= new List<MenuItemConfig>();
    }

    private static MenuConfig CreateDefault() => new()
    {
        Hotkey = "Ctrl+B",
        CloseAfterAction = true,
        MenuSize = 640,
        Items =
        [
            new MenuItemConfig
            {
                Label = "Блокнот",
                Action = new ActionConfig { Type = "launch", Path = "notepad.exe", ProcessName = "notepad" },
            },
            new MenuItemConfig
            {
                Label = "Проводник",
                Action = new ActionConfig { Type = "launch", Path = "explorer.exe" },
            },
            new MenuItemConfig
            {
                Label = "Калькулятор",
                Action = new ActionConfig { Type = "launch", Path = "calc.exe", ProcessName = "CalculatorApp" },
            },
            new MenuItemConfig
            {
                Label = "Утилиты",
                Children =
                [
                    new MenuItemConfig
                    {
                        Label = "Диспетчер задач",
                        Action = new ActionConfig { Type = "launch", Path = "taskmgr.exe" },
                    },
                    new MenuItemConfig
                    {
                        Label = "Выполнить",
                        Action = new ActionConfig { Type = "launch", Path = "shell:AppsFolder" },
                    },
                    new MenuItemConfig
                    {
                        Label = "Остановить блокнот",
                        Action = new ActionConfig { Type = "stop", ProcessName = "notepad" },
                    },
                ],
            },
            new MenuItemConfig
            {
                Label = "GitHub",
                Action = new ActionConfig { Type = "launch", Path = "https://github.com/vilminessa" },
            },
        ],
    };
}
