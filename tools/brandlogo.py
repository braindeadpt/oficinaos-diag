"""Símbolo OficinaOS (squircle macOS/iOS do rebrand «Azul Safira + Menta», PR #47) em Pillow.

Geometria replicada de src/components/Logo.astro (caixa 40×40):
base azul safira #2563eb com cantos rx 11.5, brilho menta no canto inferior direito,
reflexo especular vítreo no topo, rebordo interior fino, anel de precisão branco,
pentalobe geométrica limpa e ponto central azul.

Usado por tools/gen-og.py e tools/gen-brand.py. Desenha com supersampling (8×) e
reduz com LANCZOS — arestas limpas em qualquer tamanho.
"""

from __future__ import annotations

SAPPHIRE = (37, 99, 235)  # #2563eb — --oos-color-primary / --oos-logo-mark
MINT = (16, 185, 129)  # #10b981 — --oos-mint
WHITE = (255, 255, 255)

SS = 8  # supersampling


def _quarter_arc(cx: float, cy: float, r: float, a0: float, a1: float, n: int = 24) -> list[tuple[float, float]]:
    """Pontos de um quarto de círculo (ângulos em graus, 0 = este, 90 = sul — eixo y para baixo)."""
    import math

    return [
        (cx + r * math.cos(math.radians(a0 + (a1 - a0) * i / n)), cy + r * math.sin(math.radians(a0 + (a1 - a0) * i / n)))
        for i in range(n + 1)
    ]


def _cubic(p0, p1, p2, p3, n: int = 24) -> list[tuple[float, float]]:
    pts = []
    for i in range(1, n + 1):
        t = i / n
        mt = 1 - t
        x = mt**3 * p0[0] + 3 * mt**2 * t * p1[0] + 3 * mt * t**2 * p2[0] + t**3 * p3[0]
        y = mt**3 * p0[1] + 3 * mt**2 * t * p1[1] + 3 * mt * t**2 * p2[1] + t**3 * p3[1]
        pts.append((x, y))
    return pts


def _mint_corner() -> list[tuple[float, float]]:
    """M40 20 V28.5 A11.5 11.5 0 0 1 28.5 40 H18 C25 36.5 35.5 29 40 20 Z"""
    pts = [(40.0, 20.0), (40.0, 28.5)]
    pts += _quarter_arc(28.5, 28.5, 11.5, 0, 90)[1:]  # de (40,28.5) a (28.5,40), centro (28.5,28.5)
    pts += [(18.0, 40.0)]
    pts += _cubic((18.0, 40.0), (25.0, 36.5), (35.5, 29.0), (40.0, 20.0))
    return pts


def _specular() -> list[tuple[float, float]]:
    """M11.5 1.25 H28.5 A10.25 10.25 0 0 1 38.75 11.5 V14 C31 10.5 22 9.5 1.25 14 V11.5 A10.25 10.25 0 0 1 11.5 1.25 Z"""
    pts = [(11.5, 1.25), (28.5, 1.25)]
    pts += _quarter_arc(28.5, 11.5, 10.25, -90, 0)[1:]  # de (28.5,1.25) a (38.75,11.5)
    pts += [(38.75, 14.0)]
    pts += _cubic((38.75, 14.0), (31.0, 10.5), (22.0, 9.5), (1.25, 14.0))
    pts += [(1.25, 11.5)]
    pts += _quarter_arc(11.5, 11.5, 10.25, 180, 270)[1:]  # de (1.25,11.5) a (11.5,1.25)
    return pts


STAR = [
    (20.0, 12.6), (21.9, 17.1), (26.8, 17.5), (23.0, 20.8), (24.2, 25.6),
    (20.0, 23.0), (15.8, 25.6), (17.0, 20.8), (13.2, 17.5), (18.1, 17.1),
]


def draw_squircle(px: int) -> "object":
    """Devolve uma imagem RGBA px×px com o símbolo (a caixa 40×40 escala para px)."""
    from PIL import Image, ImageDraw

    S = px * SS
    k = S / 40.0  # fator de escala da caixa 40×40 para o bitmap supersampled

    def sc(pts):
        return [(x * k, y * k) for x, y in pts]

    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))

    # base squircle
    base = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    ImageDraw.Draw(base).rounded_rectangle((0, 0, S - 1, S - 1), radius=11.5 * k, fill=SAPPHIRE + (255,))
    silhouette = base.getchannel("A").copy()

    # brilho menta (48%) contido no squircle
    mint = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    ImageDraw.Draw(mint).polygon(sc(_mint_corner()), fill=MINT + (122,))
    mint.putalpha(Image.composite(mint.getchannel("A"), Image.new("L", (S, S), 0), base.getchannel("A").point(lambda a: 255 if a else 0)))
    base.alpha_composite(mint)

    # reflexo especular (18%)
    spec = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    ImageDraw.Draw(spec).polygon(sc(_specular()), fill=WHITE + (46,))
    spec.putalpha(Image.composite(spec.getchannel("A"), Image.new("L", (S, S), 0), base.getchannel("A").point(lambda a: 255 if a else 0)))
    base.alpha_composite(spec)

    # elementos translúcidos em camadas próprias com alpha_composite — ImageDraw
    # sobrepõe o canal alfa em vez de misturar, o que deixaria o rebordo semi-transparente
    def layer():
        return Image.new("RGBA", (S, S), (0, 0, 0, 0))

    # rebordo interior fino (branco 30%, 1.2)
    rim = layer()
    ImageDraw.Draw(rim).rounded_rectangle(
        (0.75 * k, 0.75 * k, 39.25 * k, 39.25 * k),
        radius=10.75 * k,
        outline=WHITE + (77,),
        width=max(1, round(1.2 * k)),
    )
    base.alpha_composite(rim)

    # anel de precisão (branco 96%, 2.4)
    ring = layer()
    ImageDraw.Draw(ring).ellipse(
        ((20 - 11.5) * k, (20 - 11.5) * k, (20 + 11.5) * k, (20 + 11.5) * k),
        outline=WHITE + (245,),
        width=max(1, round(2.4 * k)),
    )
    base.alpha_composite(ring)

    details = ImageDraw.Draw(base)
    # pentalobe geométrica
    details.polygon(sc(STAR), fill=WHITE + (255,))
    # ponto central azul
    details.ellipse(((20 - 2.2) * k, (20 - 2.2) * k, (20 + 2.2) * k, (20 + 2.2) * k), fill=SAPPHIRE + (255,))

    # corta a composição à silhueta exata do squircle (elimina fugas de subpíxel das curvas)
    from PIL import ImageChops

    base.putalpha(ImageChops.multiply(base.getchannel("A"), silhouette))

    return base.resize((px, px), Image.LANCZOS)
