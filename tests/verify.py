"""End-to-end tests; all generated fixtures stay in a disposable test directory."""
from pathlib import Path
import hashlib
import subprocess
import tempfile

from pypdf import PdfReader, PdfWriter
from reportlab.pdfgen import canvas

ROOT = Path(__file__).resolve().parents[1]
APP = ROOT / "PdfSplitter.exe"


def invoke(pdf, *args, password=None):
    command = [str(APP), "--split", str(pdf), "--settings", str(pdf.parent / "test-settings.xml"), *args]
    if password is not None:
        command.append("--password-stdin")
    return subprocess.run(command, input=(password + "\n") if password is not None else "",
                          encoding="utf-8", errors="replace", capture_output=True, timeout=60)


def check_pages(original, folder, password=None, pattern="{name}_page{page}", digits=1):
    reader = PdfReader(original, password=password)
    files = list(folder.glob("*.pdf"))
    assert len(files) == len(reader.pages), (len(files), len(reader.pages))
    for number, original_page in enumerate(reader.pages, 1):
        import re
        name = re.sub(r"\{name\}|\{page\}", lambda m: original.stem if m[0] == "{name}" else str(number).zfill(digits), pattern)
        output = folder / (name if name.lower().endswith(".pdf") else name + ".pdf")
        result = PdfReader(output)
        assert not result.is_encrypted
        assert len(result.pages) == 1
        page = result.pages[0]
        assert page.extract_text() == original_page.extract_text(), number
        assert page.mediabox == original_page.mediabox, number
        assert page.rotation == original_page.rotation, number
        assert page.get_contents().get_data() == original_page.get_contents().get_data(), number


def main():
    with tempfile.TemporaryDirectory(prefix="splitter-tests-", dir=ROOT / "tests") as work:
        directory = Path(work) / "中文 路径 %d & (test)"
        directory.mkdir()
        sample = directory / "L01 中文 %d & (课堂).PDF"
        drawing = canvas.Canvas(str(sample))
        for page in range(1, 13):
            drawing.setPageSize((420 + page, 600 + page))
            drawing.drawString(40, 500, f"Test page {page} / 12")
            drawing.setFillColorRGB(page / 12, 0.3, 0.7)
            drawing.rect(40, 100, 120 + page, 180, fill=1)
            drawing.showPage()
        drawing.save()
        original_hash = hashlib.sha256(sample.read_bytes()).digest()
        result = invoke(sample, "--end-page", "12")
        assert result.returncode == 0, result.stderr
        first = directory / f"{sample.stem}_clips"
        check_pages(sample, first)
        assert hashlib.sha256(sample.read_bytes()).digest() == original_hash
        print("PASS: 12 pages, numeric names, Chinese/spaces/metacharacters, content and source integrity")

        marker = first / "keep.txt"
        marker.write_text("keep", encoding="utf-8")
        result = invoke(sample, "--end-page", "12")
        assert result.returncode == 1, result.stderr
        assert "已存在" in result.stderr and "正在" not in result.stderr
        assert not (directory / f"{sample.stem}_clips_2").exists()
        check_pages(sample, first)
        assert marker.read_text(encoding="utf-8") == "keep"
        print("PASS: automatic mode stops before qpdf on an existing directory; contents preserved")

        single = directory / "one.pdf"
        writer = PdfWriter()
        writer.add_page(PdfReader(sample).pages[3])
        writer.pages[0].rotate(90)
        writer.write(single)
        (directory / "one_clips").write_text("existing file", encoding="utf-8")
        result = invoke(single)
        assert result.returncode == 1, result.stderr
        assert "已存在" in result.stderr and "正在" not in result.stderr
        assert (directory / "one_clips").read_text(encoding="utf-8") == "existing file"
        assert not (directory / "one_clips_2").exists()
        result = invoke(single, "--folder-pattern", "{name}_rotated")
        assert result.returncode == 0, result.stderr
        check_pages(single, directory / "one_rotated")
        print("PASS: automatic mode rejects an occupied file path; rotated single-page integrity")

        existing = directory / "已有文件夹 中文 %d & (切片)"
        existing.mkdir()
        (existing / "keep.txt").write_text("keep", encoding="utf-8")
        (existing / "keep-subfolder").mkdir()
        result = invoke(sample, "--end-page", "12", "--output-directory", str(existing))
        assert result.returncode == 0, result.stderr
        assert Path(result.stdout.strip()) == existing
        check_pages(sample, existing)
        assert (existing / "keep.txt").read_text(encoding="utf-8") == "keep"
        assert (existing / "keep-subfolder").is_dir()
        assert not list(existing.glob(".pdf-splitter-*"))
        assert not (existing / f"{sample.stem}_clips").exists()
        before = {p: p.read_bytes() for p in existing.iterdir() if p.is_file()}
        result = invoke(sample, "--end-page", "12", "--output-directory", str(existing))
        assert result.returncode == 1, result.stderr
        assert "同名" in result.stderr and "正在拆分" not in result.stderr
        assert {p: p.read_bytes() for p in existing.iterdir() if p.is_file()} == before
        print("PASS: direct output into a Unicode existing folder; existing files and subfolders preserved")

        occupied = directory / "末页冲突"
        occupied.mkdir()
        (occupied / f"{sample.stem}_page12.pdf").mkdir()
        result = invoke(sample, "--end-page", "12", "--output-directory", str(occupied))
        assert result.returncode == 1, result.stderr
        assert "同名" in result.stderr and "正在拆分" not in result.stderr
        assert len(list(occupied.iterdir())) == 1
        print("PASS: a directory occupying the last page name blocks the entire run before splitting")

        custom = directory / "现有 自定义命名"
        custom.mkdir()
        result = invoke(single, "--output-directory", str(custom), "--file-pattern", "第{page}页_{name}.pdf", "--page-digits", "3")
        assert result.returncode == 0, result.stderr
        check_pages(single, custom, pattern="第{page}页_{name}.pdf", digits=3)
        single_hash = hashlib.sha256(single.read_bytes()).digest()
        result = invoke(single, "--output-directory", str(directory))
        assert result.returncode == 0, result.stderr
        assert len(PdfReader(directory / "one_page1.pdf").pages) == 1
        assert hashlib.sha256(single.read_bytes()).digest() == single_hash
        print("PASS: custom naming in an existing folder; selecting the source folder preserves the input")

        missing_output = directory / "不存在的输出"
        for target in (str(missing_output), str(single), "", '""'):
            before = set(directory.iterdir())
            result = invoke(single, "--output-directory", target)
            assert result.returncode == 1, (target, result.stderr)
            assert "正在" not in result.stderr
            assert set(directory.iterdir()) == before
        assert not missing_output.exists()
        print("PASS: missing, file, and empty output paths rejected without creating directories")

        encrypted = directory / "加密.pdf"
        writer = PdfWriter()
        writer.add_page(PdfReader(sample).pages[0])
        secret = '中文 密码 " & %d'
        writer.encrypt(secret)
        writer.write(encrypted)
        failed_output = directory / "失败时保留"
        failed_output.mkdir()
        (failed_output / "keep.txt").write_text("keep", encoding="utf-8")
        assert invoke(encrypted, "--output-directory", str(failed_output), password="wrong").returncode == 1
        assert [p.name for p in failed_output.iterdir()] == ["keep.txt"]
        assert invoke(encrypted, password="wrong").returncode == 1
        assert not (directory / "加密_clips").exists()
        result = invoke(encrypted, password=secret)
        assert result.returncode == 0, result.stderr
        check_pages(encrypted, directory / "加密_clips", password=secret)
        print("PASS: encrypted input, UTF-8 password over stdin, wrong password rejected")

        invalid = directory / "invalid.pdf"
        invalid.write_bytes(b"This is not a PDF")
        assert invoke(invalid).returncode == 1
        empty = directory / "empty.pdf"
        PdfWriter().write(empty)
        assert invoke(empty).returncode == 1
        assert invoke(directory / "missing.pdf").returncode == 1
        assert invoke(sample, "--end-page", "12", "--qpdf", str(directory / "missing.exe")).returncode == 1
        assert not (directory / "invalid_clips").exists()
        assert not (directory / "empty_clips").exists()
        assert not list(directory.glob(".pdf-splitter-*"))
        print("PASS: invalid/empty/missing input and missing qpdf; no partial output")

        recovered = directory / "recovered.pdf"
        source_bytes = sample.read_bytes()
        import re
        recovered.write_bytes(re.sub(rb"startxref\s+\d+", b"startxref\n0", source_bytes))
        result = invoke(recovered, "--end-page", "12")
        assert result.returncode == 0, result.stderr
        check_pages(recovered, directory / "recovered_clips")
        assert "WARNING" in result.stderr
        print("PASS: recoverable qpdf warnings still yield complete output")

        pattern = "{name}-第{page}页.pdf"
        result = invoke(sample, "--end-page", "12", "--folder-pattern", "切片_{name}_完成", "--file-pattern", pattern, "--page-digits", "3")
        assert result.returncode == 0, result.stderr
        check_pages(sample, directory / f"切片_{sample.stem}_完成", pattern=pattern, digits=3)
        print("PASS: custom folder and file naming; padded page numbers; one PDF extension")

        literal = directory / "实际{page}{name}.pdf"
        literal.write_bytes(single.read_bytes())
        result = invoke(literal)
        assert result.returncode == 0, result.stderr
        check_pages(literal, directory / f"{literal.stem}_clips")
        part = directory / "part.pdf"
        part.write_bytes(single.read_bytes())
        result = invoke(part, "--file-pattern", "{name}-{page}")
        assert result.returncode == 0, result.stderr
        check_pages(part, directory / "part_clips", pattern="{name}-{page}")
        print("PASS: literal template characters in source names; raw/final naming collisions")

        for option, value in [
            ("--folder-pattern", "fixed"), ("--folder-pattern", "../{name}"),
            ("--folder-pattern", "{name}_{unknown}"), ("--folder-pattern", "{name}."),
            ("--file-pattern", "{name}"), ("--file-pattern", "{page}"),
            ("--file-pattern", "{name}/{page}"), ("--file-pattern", "{name}_{page} "),
            ("--page-digits", "0"), ("--page-digits", "9"),
        ]:
            before = set(directory.iterdir())
            result = invoke(single, option, value)
            assert result.returncode == 1, (option, value, result.stderr)
            assert set(directory.iterdir()) == before
        print("PASS: invalid naming rules rejected before creating output")

        settings_path = directory / "test-settings.xml"
        settings_path.write_text('''<AppSettings><QpdfPath></QpdfPath><FolderPattern>{name}_自定义</FolderPattern><FilePattern>{name}_p{page}</FilePattern><PageDigits>2</PageDigits></AppSettings>''', encoding="utf-8")
        result = invoke(single)
        assert result.returncode == 0, result.stderr
        check_pages(single, directory / "one_自定义", pattern="{name}_p{page}", digits=2)
        assert not list(directory.glob(".pdf-splitter-*"))
        print("PASS: saved settings loaded by a fresh process")
        assert not list(directory.rglob(".pdf-splitter-*"))
        assert hashlib.sha256(sample.read_bytes()).digest() == original_hash
    print("All integration checks passed.")


if __name__ == "__main__":
    main()
