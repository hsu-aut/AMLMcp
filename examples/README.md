# Examples

Four small documents to try the server on.

| File | What it shows |
|---|---|
| `plant.aml` | Three views of one plant in one document, linked across hierarchies |
| `DemoLib.aml` | The external class library that `plant.aml` refers to |
| `plant.amlx` | The same model as an AMLX container, with a geometry part |
| `legacy-caex215.aml` | A CAEX 2.15 document |

`plant.aml` holds a process view (`ClampWorkpiece`, `DrillHole`), a behaviour view (`Free`,
`Drilling`) and a plant structure (`Line/DrillingStation`). The two `InternalLink` elements
`StepToStation` and `StationToBehavior` tie them together, so `find_path` from `DrillHole` to
`Drilling` answers in two steps. It also carries the constructs that are easy to get wrong: an
external reference with an alias, an inherited attribute (`Manufacturer` comes from the library, only
`SpindleSpeed` is on the instance), and a mirror object.

`plant.amlx` carries the same model, with the external reference written as `../lib/DemoLib.aml`.
Asking it and asking `plant.aml` gives the same answers; only `open_aml_document` differs, because it
names the parts of the package.

The documents are committed as they are and can be edited. `plant.amlx` is an ordinary ZIP:

```
[Content_Types].xml
_rels/.rels                    RootDocument -> /model/plant.aml
model/plant.aml
model/_rels/plant.aml.rels     Library -> /lib/DemoLib.aml, Collada -> /geometry/station.dae
lib/DemoLib.aml
geometry/station.dae
```

For a full model of a real machine, with a plant structure, a VDI/VDE 3682 process description and a
Petri net coupled through object references, point the server at the MPS500 material with `--root`.
