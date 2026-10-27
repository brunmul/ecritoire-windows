# Écritoire pour Windows

Version Windows native d'Écritoire (C#, .NET 8, WPF, éditeur AvalonEdit). Elle lit et écrit les classeurs au même
format que l'app macOS : dossiers de fichiers Markdown avec en-tête YAML, ordre manuel (`.order`), icônes
(`.icones`) et fichiers annexes (`.ecritoire/`). Un classeur ouvert sur Windows se rouvre sur Mac sans aucune perte,
et inversement.

## Étape 1 (cette version)

- Ouvrir un classeur ; le classeur en arbre, avec son ordre et ses icônes.
- Éditeur mis en page comme sur Mac : titres, corps, intro, citations en Grotesk grise avec un filet, gras, italique,
  liens, surlignages, images `![[…]]`. Les signes Markdown sont masqués, sauf dans le paragraphe du curseur.
- Enregistrement automatique 0,8 s après la dernière frappe, en changeant de document et en quittant.
- Recherche dans les titres et le texte (Ctrl+Maj+F), sans tenir compte des accents.
- Affichage poème : versets `[n]` numérotés dans la marge, bloc titre centré, renumérotation automatique.
- Zoom (Ctrl++, Ctrl+−, Ctrl+0).

## Construire

Il faut le SDK .NET 8. La construction marche depuis Windows, macOS ou Linux :

```sh
dotnet test                                   # vérifications du cœur (format, classeur, poème, recherche)
dotnet publish src/Ecritoire.App -c Release -o publish
```

Le résultat est un seul fichier `publish/Ecritoire.exe` (Windows 10 et 11, 64 bits), qui n'a besoin d'aucune
installation de .NET.

## Versions et mises à jour

Une étiquette `vX.Y.Z` poussée sur GitHub lance `.github/workflows/version.yml` : tests du format, exe autonome,
installateur Inno Setup (`installateur/Ecritoire.iss`, installation sans droits administrateur), puis publication dans
les releases de ce dépôt. Au lancement, l'app
regarde la dernière version publiée ; si elle est plus récente, un bouton « Mettre à jour » apparaît dans l'en-tête :
l'installateur est téléchargé, lancé sans questions, et Écritoire se rouvre.

## Polices

L'app embarque deux polices libres (licence SIL Open Font License, dans `src/Ecritoire.App/Polices`) : Hanken Grotesk
pour le texte, l'intro, les sous-titres et l'interface, et Libre Bodoni, une didone, pour les grands titres. Elles sont
choisies dans l'esprit des typographies du Monde diplomatique et des éditions La Fabrique. Aucune police payante n'est
utilisée ni distribuée.

## Compatibilité avec l'app macOS

`tests/Ecritoire.Core.Tests/CompatTests.cs` compare, octet par octet, ce que l'app Windows écrit à ce que le code Swift
de l'app macOS écrit, sur des copies de classeurs réels (variables `ECRITOIRE_CORPUS` et `ECRITOIRE_ATTENDU`).

## Licence

GPL-3.0-or-later : voir [LICENSE](LICENSE).
