using System;
using ISC.AI.Modules.Media.Data;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Media;

/// <summary>
/// ТБ-030: одна выдача файла субъекту — одна запись аудита. Первое обращение аудируется всегда (обход через
/// заголовок Range невозможен), повторные в окне — нет, другой субъект или другой файл — снова да.
/// </summary>
public sealed class MediaViewAuditThrottleTests
{
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact(DisplayName = "Первое обращение аудируется, повтор в окне — нет, после окна — снова да")]
    public void First_access_audited_repeat_in_window_not()
    {
        var time = new FixedTime(new DateTimeOffset(2026, 9, 18, 10, 0, 0, TimeSpan.Zero));
        var throttle = new MediaViewAuditThrottle(time, TimeSpan.FromMinutes(5));

        throttle.ShouldAudit(7, "media:file:media-originals:1:a.mp4").ShouldBeTrue();
        throttle.ShouldAudit(7, "media:file:media-originals:1:a.mp4").ShouldBeFalse();
        time.Now = time.Now.AddMinutes(4);
        throttle.ShouldAudit(7, "media:file:media-originals:1:a.mp4").ShouldBeFalse();
        time.Now = time.Now.AddMinutes(2);
        throttle.ShouldAudit(7, "media:file:media-originals:1:a.mp4").ShouldBeTrue();
    }

    [Fact(DisplayName = "Другой субъект и другой файл аудируются независимо")]
    public void Different_subject_or_file_is_independent()
    {
        var throttle = new MediaViewAuditThrottle(new FixedTime(DateTimeOffset.UtcNow));

        throttle.ShouldAudit(7, "media:file:media-faces:1:x.jpg").ShouldBeTrue();
        throttle.ShouldAudit(8, "media:file:media-faces:1:x.jpg").ShouldBeTrue();
        throttle.ShouldAudit(7, "media:file:media-faces:1:y.jpg").ShouldBeTrue();
        throttle.ShouldAudit(null, "media:file:media-faces:1:x.jpg").ShouldBeTrue();
        throttle.ShouldAudit(null, "media:file:media-faces:1:x.jpg").ShouldBeFalse();
    }
}
