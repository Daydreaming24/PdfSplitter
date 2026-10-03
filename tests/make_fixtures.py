"""Generate the PDF fixtures used by the standalone C# harnesses."""
from pathlib import Path
import subprocess
import sys

from reportlab.pdfgen import canvas

PASSWORD = "中文 test & password"


def make(path, pages):
    c = canvas.Canvas(str(path))
    for number in range(1, pages + 1):
        c.drawString(72, 720, f"Page {number}")
        c.showPage()
    c.save()


def main():
    folder = Path(sys.argv[1] if len(sys.argv) > 1 else Path(__file__).parent / "qa-output" / "fixtures")
    folder.mkdir(parents=True, exist_ok=True)
    make(folder / "sample.pdf", 12)
    make(folder / "large.pdf", 3000)
    encrypted = folder / "encrypted.pdf"
    make(encrypted, 7)
    # AES-256 keeps the non-ASCII password intact; pypdf's default RC4 cannot encode it.
    subprocess.run(["qpdf", "--encrypt", PASSWORD, PASSWORD, "256", "--", "--replace-input", str(encrypted)], check=True)
    print(folder.resolve())


if __name__ == "__main__":
    main()
