using ISC.AI.Backup.Restore;

// Программа восстановления из резервной копии (ADR-0063): разбор команды и ввод — в RestoreApp, здесь только консоль.
Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.InputEncoding = System.Text.Encoding.UTF8;
return await RestoreApp.RunAsync(args, new SystemRestoreConsole());
