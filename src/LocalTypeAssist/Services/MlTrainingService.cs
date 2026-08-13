using System.Diagnostics;
using LocalTypeAssist.Models;

namespace LocalTypeAssist.Services;

public static class MlTrainingService
{
    public static async Task<string> TrainAsync(string profileName, CancellationToken cancellationToken = default)
    {
        var scriptPath = Path.Combine(AppContext.BaseDirectory, "Resources", "ml", "train_personal_model.py");
        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException("Не найден локальный Python-скрипт обучения.", scriptPath);
        }

        var outputPath = AppSettings.GetMlModelPath(profileName);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var localVenvPython = Path.Combine(AppSettings.DataRoot, "ml-venv", "Scripts", "python.exe");
        var attempts = new[]
        {
            new PythonCommand(localVenvPython, Array.Empty<string>()),
            new PythonCommand("py", new[] { "-3" }),
            new PythonCommand("python", Array.Empty<string>()),
            new PythonCommand("python3", Array.Empty<string>())
        };

        Exception? lastException = null;
        foreach (var attempt in attempts)
        {
            try
            {
                return await RunPythonAsync(attempt, scriptPath, profileName, outputPath, cancellationToken);
            }
            catch (System.ComponentModel.Win32Exception exception)
            {
                lastException = exception;
            }
        }

        throw new InvalidOperationException(
            "Python не найден. Установите Python 3 и запустите scripts\\setup-ml.ps1 один раз.",
            lastException);
    }

    private static async Task<string> RunPythonAsync(
        PythonCommand python,
        string scriptPath,
        string profileName,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = python.Executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in python.PrefixArguments)
        {
            startInfo.ArgumentList.Add(arg);
        }
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("--db");
        startInfo.ArgumentList.Add(LearningEventStore.DatabasePath);
        startInfo.ArgumentList.Add("--profile");
        startInfo.ArgumentList.Add(LocalLearningStore.SanitizeProfileName(profileName));
        startInfo.ArgumentList.Add("--output");
        startInfo.ArgumentList.Add(outputPath);

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stdout = (await stdoutTask).Trim();
        var stderr = (await stderrTask).Trim();

        if (process.ExitCode != 0)
        {
            if (stderr.Contains("No module named", StringComparison.OrdinalIgnoreCase) ||
                stderr.Contains("scikit-learn", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Для ML-обучения не хватает Python-зависимостей. Запустите scripts\\setup-ml.ps1 один раз.\n\n" + stderr);
            }

            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr)
                ? $"Python завершился с кодом {process.ExitCode}."
                : stderr);
        }

        return string.IsNullOrWhiteSpace(stdout) ? "ML-модель обучена." : stdout;
    }

    private sealed record PythonCommand(string Executable, IReadOnlyList<string> PrefixArguments);
}
