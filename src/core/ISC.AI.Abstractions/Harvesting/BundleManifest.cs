namespace ISC.AI.Abstractions.Harvesting;

/// <summary>
/// Индекс шардированного пакета (Э4-17) — контракт «через зазор» для больших корпусов (170К+): вместо
/// одного гигантского массива пакет разбит на файлы-шарды, а <c>manifest.json</c> перечисляет их.
/// Пишется сборщиком вне контура (<c>StreamingBundleWriter</c>), читается импортёром в контуре.
/// </summary>
/// <param name="Total">Всего документов в пакете.</param>
/// <param name="ShardSize">Документов на один шард.</param>
/// <param name="Shards">Имена файлов-шардов (в том же каталоге, что и <c>manifest.json</c>); каждый — массив документов.</param>
public sealed record BundleManifest(int Total, int ShardSize, IReadOnlyList<string> Shards);
