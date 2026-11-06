// Rendu « encre » du texte, d'après les filtres SVG de Papertooth (https://github.com/tbudin/papertooth, licence MIT,
// Copyright (c) 2026 Plain Matter Pte Ltd) : la même chaîne que leurs filtres « pt-ink-text », « pt-ink-head » et
// « typewriter », réécrite en un seul pixel shader pour WPF.
//
//   1. déplacement : les bords de l'encre ondulent (feTurbulence + feDisplacementMap) ;
//   2. bavure : flou léger puis seuil sur l'opacité, l'encre gonfle et arrondit les angles (feGaussianBlur +
//      feComponentTransfer) ;
//   3. plaques : la couverture d'encre varie par grandes taches (feTurbulence basse fréquence + feColorMatrix) ;
//   4. manques : de petits trous où le papier n'a pas pris l'encre.
//
// Chaque style (Encre, Bords, Machine à écrire) n'est qu'un jeu de réglages de cette chaîne, choisi côté C#
// (InkEffect.cs) : il n'y a aucune branche ici.
//
// Les bruits sont mesurés en pixels indépendants de l'écran (DIP), comme les unités CSS de Papertooth, et ancrés au
// document : le grain reste collé aux lettres quand on fait défiler.
//
// Compilation (Windows SDK) : fxc /nologo /T ps_3_0 /E main /O3 /Fo Encre.ps Encre.fx
// (fait par .github/workflows/shader.yml ; Encre.ps est versionné à côté de ce fichier).

sampler2D input : register(s0);

float4 geometry : register(c0);   // xy : taille de la couche (DIP) ; zw : 1 / taille
float4 pixel    : register(c1);   // xy : un pixel de l'écran en coordonnées 0..1 ; z, w : poids du flou (côté, coin)
float4 disp     : register(c2);   // x : amplitude du déplacement (DIP) ; y : fréquence ; zw : origine du bruit
float4 blot     : register(c3);   // xy : fréquences des plaques ; zw : origine du bruit
float4 density  : register(c4);   // x, y : opacité = saturate(x * plaque + y) ; z : part de la bavure ; w : part des manques
float4 spread   : register(c5);   // x, y : seuil de la bavure, alpha = saturate(x * a + y) ; z : fréquence des manques
float4 wear     : register(c6);   // xy : origine du bruit des manques ; z, w : trous = saturate(z * bruit + w)

static const float PERIOD = 256.0; // le réseau du bruit se répète tous les 256 pas (les origines sont prises modulo 256)

float2 hash22(float2 c)
{
    c = c - PERIOD * floor(c / PERIOD);
    float3 p3 = frac(float3(c.xyx) * float3(0.1031, 0.1030, 0.0973));
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.xx + p3.yz) * p3.zy);
}

float2 gradient(float2 c, float seed)
{
    float2 g = hash22(c + seed) * 2.0 - 1.0;
    return g * rsqrt(dot(g, g) + 1e-6);
}

// Bruit de gradient (Perlin), entre -0,7 et 0,7 environ, comme celui de feTurbulence.
float perlin(float2 q, float seed)
{
    float2 i = floor(q);
    float2 f = q - i;
    float2 u = f * f * (3.0 - 2.0 * f);
    float a = dot(gradient(i, seed), f);
    float b = dot(gradient(i + float2(1, 0), seed), f - float2(1, 0));
    float c = dot(gradient(i + float2(0, 1), seed), f - float2(0, 1));
    float d = dot(gradient(i + float2(1, 1), seed), f - float2(1, 1));
    return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
}

// feTurbulence type="fractalNoise" : somme des octaves, ramenée entre 0 et 1.
float fractal1(float2 q, float seed) { return perlin(q, seed) * 0.5 + 0.5; }
float fractal2(float2 q, float seed) { return (perlin(q, seed) + 0.5 * perlin(q * 2.0, seed)) * 0.5 + 0.5; }

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 local = uv * geometry.xy;

    // 1. Déplacement des bords.
    float2 dq = local * disp.y + disp.zw;
    float2 d = float2(fractal1(dq, 13.0), fractal1(dq, 101.0)) - 0.5;
    float2 at = uv + disp.x * d * geometry.zw;

    float4 sharp = tex2D(input, at);

    // 2. Bavure : flou 3 × 3 autour du point déplacé, puis seuil sur l'opacité (couleur gardée).
    float2 px = pixel.xy;
    float4 sum = sharp
        + pixel.z * (tex2D(input, at + float2(px.x, 0)) + tex2D(input, at - float2(px.x, 0))
                   + tex2D(input, at + float2(0, px.y)) + tex2D(input, at - float2(0, px.y)))
        + pixel.w * (tex2D(input, at + px) + tex2D(input, at - px)
                   + tex2D(input, at + float2(px.x, -px.y)) + tex2D(input, at + float2(-px.x, px.y)));
    float4 soft = sum / (1.0 + 4.0 * pixel.z + 4.0 * pixel.w);
    float alpha = saturate(spread.x * soft.a + spread.y);
    float3 color = soft.rgb / max(soft.a, 1e-4);
    float4 swollen = float4(color * alpha, alpha);
    // Seule l'encre sombre bave : un surlignage jaune transparent garde son opacité au lieu de devenir plein.
    float inkiness = saturate((0.8 - dot(color, float3(0.2126, 0.7152, 0.0722))) * 4.0);
    float4 inked = lerp(sharp, swollen, density.z * inkiness);

    // 3. Plaques d'encre.
    float plate = fractal2(local * blot.xy + blot.zw, 311.0);
    float cover = saturate(density.x * plate + density.y);

    // 4. Manques.
    float w = fractal1(local * spread.z + wear.xy, 733.0);
    float holes = lerp(1.0, saturate(wear.z * w + wear.w), density.w);

    return inked * (cover * holes);
}
