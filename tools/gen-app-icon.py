"""Gera src/OficinaDiag/Assets/app.ico a partir do símbolo da marca (brandlogo.draw_squircle).

O símbolo é o canónico do rebrand «Azul Safira + Menta» — o mesmo do site
(oficinaos-website/tools/brandlogo.py, copiado verbatim para aqui; se a marca
mudar, copia-se o módulo novo e corre-se outra vez). O .ico leva as resoluções
clássicas do Windows (16–256, PNG-comprimidas nas maiores).

Uso: python3 tools/gen-app-icon.py
"""

from pathlib import Path

from brandlogo import draw_squircle

ROOT = Path(__file__).resolve().parent.parent
SIZES = [16, 24, 32, 48, 64, 128, 256]


def main() -> None:
    base = draw_squircle(256)
    dst = ROOT / "src/OficinaDiag/Assets/app.ico"
    # O writer ICO do Pillow deriva todas as resoluções do frame passado —
    # append_images não é suportado neste formato (saía um .ico só com 16px).
    base.save(dst, format="ICO", sizes=[(s, s) for s in SIZES])
    print(f"[ok] {dst} ({len(SIZES)} resoluções)")


if __name__ == "__main__":
    main()
