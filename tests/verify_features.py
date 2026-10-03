"""Range splitting, numbering policies, and explicit overwrite confirmation."""
from pathlib import Path
import hashlib
import os
import tempfile

from pypdf import PdfReader, PdfWriter
from reportlab.pdfgen import canvas
from verify import ROOT, invoke


def ok(pdf, *args, **kwargs):
    result = invoke(pdf, *args, **kwargs)
    assert result.returncode == 0, result.stderr
    return result


def content_matches(source, target, first, last, password=None):
    original = PdfReader(source, password=password)
    result = PdfReader(target)
    assert not result.is_encrypted
    assert len(result.pages) == last - first + 1
    for a, b in zip(original.pages[first - 1:last], result.pages):
        assert a.extract_text() == b.extract_text()
        assert a.mediabox == b.mediabox
        assert a.rotation == b.rotation
        assert a.get_contents().get_data() == b.get_contents().get_data()


def main():
    with tempfile.TemporaryDirectory(prefix="features-", dir=ROOT / "tests") as work:
        root = Path(work) / "中文 %d & {start}"
        root.mkdir()
        source = root / "讲义 {end}.pdf"
        drawing = canvas.Canvas(str(source))
        for page in range(1, 7):
            drawing.setPageSize((400 + page, 600 + page))
            drawing.drawString(50, 500, f"Page {page}")
            drawing.showPage()
        drawing.save()
        original = hashlib.sha256(source.read_bytes()).digest()
        block = ["--mode", "range", "--start-page", "2", "--end-page", "5"]
        ok(source, *block)
        target = root / f"{source.stem}_pages2-5.pdf"
        content_matches(source, target, 2, 5)
        assert invoke(source, *block).returncode == 1
        ok(source, *block, "--collision", "rename")
        content_matches(source, target.with_stem(target.stem + " (1)"), 2, 5)
        output = root / "已有输出"
        output.mkdir()
        (output / "keep.txt").write_text("keep")
        ok(source, *block, "--output-directory", str(output), "--range-suffix", "课堂.PDF")
        content_matches(source, output / f"{source.stem}_课堂.PDF", 2, 5)
        ok(source, "--mode", "range", "--start-page", "6", "--end-page", "6")
        content_matches(source, root / f"{source.stem}_pages6-6.pdf", 6, 6)
        print("PASS: inclusive ranges, last/single page, custom suffix, nonempty Unicode directory, literal tokens")

        # Per-page selection keeps source page numbers and ignores collisions outside the selection.
        selected = root / "selected-pages"
        selected.mkdir()
        outside = selected / f"{source.stem}_page1.pdf"
        outside.write_text("keep")
        per_page = ["--mode", "pages", "--start-page", "2", "--end-page", "5",
                    "--output-directory", str(selected)]
        ok(source, *per_page)
        assert {p.name for p in selected.iterdir()} == {
            f"{source.stem}_page{number}.pdf" for number in range(1, 6)}
        for number in range(2, 6):
            content_matches(source, selected / f"{source.stem}_page{number}.pdf", number, number)
        assert outside.read_text() == "keep"
        before = {p.name: p.read_bytes() for p in selected.iterdir()}
        assert invoke(source, *per_page).returncode == 1
        assert {p.name: p.read_bytes() for p in selected.iterdir()} == before
        ok(source, *per_page, "--collision", "rename")
        for number in range(2, 6):
            content_matches(source, selected / f"{source.stem}_page{number} (1).pdf", number, number)
        ok(source, "--mode", "pages", "--start-page", "6", "--end-page", "6",
           "--output-directory", str(selected), "--page-digits", "3")
        content_matches(source, selected / f"{source.stem}_page006.pdf", 6, 6)
        assert not (selected / f"{source.stem}_page001.pdf").exists()
        print("PASS: per-page subsets, original page numbering, inclusive single last page, padding and selected-file collisions")

        for mode in ("pages", "range"):
            for first, last in [("0", "2"), ("3", "2"), ("1", "7"), ("7", "7"), ("", "2"),
                                ("1.5", "2"), ("1", "2147483648"), ("1", "0"), ("-1", "2")]:
                before = set(root.iterdir())
                result = invoke(source, "--mode", mode, "--start-page", first, "--end-page", last)
                assert result.returncode == 1, (mode, first, last)
                assert set(root.iterdir()) == before
        for suffix in ["", "../bad", "bad/", "bad.", "bad ", "{unknown}"]:
            assert invoke(source, *block, "--range-suffix", suffix).returncode == 1
        print("PASS: invalid and out-of-bounds ranges and unsafe suffixes")

        for suffix, expected in [("notes (2)", "notes (3)"), ("notes（005）", "notes (6)"),
                                 ("notes (draft)", "notes (draft) (1)"),
                                 ("notes (99999999999999999999)", "notes (100000000000000000000)")]:
            (output / f"{source.stem}_{suffix}.pdf").write_text("old")
            ok(source, *block, "--output-directory", str(output), "--range-suffix", suffix, "--collision", "rename")
            content_matches(source, output / f"{source.stem}_{expected}.pdf", 2, 5)
        (output / f"{source.stem}_gap.pdf").write_text("old")
        (output / f"{source.stem}_gap (1).pdf").mkdir()
        (output / f"{source.stem}_gap (4).PDF").write_text("old")
        ok(source, *block, "--output-directory", str(output), "--range-suffix", "gap", "--collision", "rename", "--numbering", "next")
        assert (output / f"{source.stem}_gap (2).pdf").exists()
        ok(source, *block, "--output-directory", str(output), "--range-suffix", "gap", "--collision", "rename", "--numbering", "max")
        assert (output / f"{source.stem}_gap (5).pdf").exists()
        print("PASS: existing numeric suffix, fullwidth parentheses, large numbers, holes, folders, extension case")

        batch = root / "batch"
        batch.mkdir()
        (batch / f"{source.stem}_page1.pdf").write_text("old")
        (batch / f"{source.stem}_page1 (2).pdf").write_text("old")
        (batch / f"{source.stem}_page2（5）.PDF").mkdir()
        (batch / "unrelated (900).pdf").write_text("old")
        ok(source, "--end-page", "6", "--output-directory", str(batch), "--collision", "rename", "--numbering", "max")
        for number in range(1, 7):
            content_matches(source, batch / f"{source.stem}_page{number} (6).pdf", number, number)
        assert not (batch / f"{source.stem}_page2.pdf").exists()
        fresh = root / "fresh"
        fresh.mkdir()
        (fresh / f"{source.stem}_page1 (100).pdf").write_text("old")
        ok(source, "--end-page", "6", "--output-directory", str(fresh), "--collision", "rename", "--numbering", "max")
        assert (fresh / f"{source.stem}_page1.pdf").exists()
        print("PASS: one collision numbers entire batch above related maximum; unrelated numbers ignored; no collision keeps names")

        # Auto-created per-page folder supports repeated runs with rename and overwrite.
        ok(source, "--end-page", "6", "--collision", "rename")
        ok(source, "--end-page", "6", "--collision", "rename")
        assert (root / f"{source.stem}_clips" / f"{source.stem}_page1 (1).pdf").exists()
        overwrite = ["--collision", "overwrite", "--confirm-overwrite"]
        result = invoke(source, *block, "--collision", "overwrite")
        assert result.returncode == 1 and "--confirm-overwrite" in result.stderr
        target.write_text("old")
        ok(source, *block, *overwrite)
        content_matches(source, target, 2, 5)
        ok(source, "--end-page", "6", *overwrite)
        assert (root / f"{source.stem}_clips" / f"{source.stem}_page1 (1).pdf").exists()
        occupied = output / f"{source.stem}_folder.pdf"
        occupied.mkdir()
        assert invoke(source, *block, "--output-directory", str(output), "--range-suffix", "folder", *overwrite).returncode == 1
        linked = output / f"{source.stem}_linked.pdf"
        os.link(source, linked)
        assert invoke(source, *block, "--output-directory", str(output), "--range-suffix", "linked", *overwrite).returncode == 1
        assert hashlib.sha256(source.read_bytes()).digest() == original
        print("PASS: both modes rename/overwrite, explicit CLI confirmation, directory and source hard-link protection")

        encrypted = root / "加密.pdf"
        writer = PdfWriter()
        reader = PdfReader(source)
        for page in reader.pages:
            writer.add_page(page)
        writer.pages[2].rotate(90)
        password = '中文 " & password'
        writer.encrypt(password)
        writer.write(encrypted)
        assert invoke(encrypted, *block, password="bad").returncode == 1
        ok(encrypted, *block, password=password)
        content_matches(encrypted, root / "加密_pages2-5.pdf", 2, 5, password)
        encrypted_pages = root / "encrypted-pages"
        encrypted_pages.mkdir()
        ok(encrypted, "--mode", "pages", "--start-page", "2", "--end-page", "5",
           "--output-directory", str(encrypted_pages), password=password)
        assert len(list(encrypted_pages.glob("*.pdf"))) == 4
        for number in range(2, 6):
            content_matches(encrypted, encrypted_pages / f"加密_page{number}.pdf", number, number, password)
        assert not list(root.rglob(".pdf-splitter-*"))
        assert hashlib.sha256(source.read_bytes()).digest() == original
        print("PASS: encrypted ranges, rotation, password handling, staging cleanup, input integrity")
    print("All feature integration checks passed.")


if __name__ == "__main__":
    main()
