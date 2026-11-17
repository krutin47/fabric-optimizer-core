# Validator failure codes

Stable identifiers returned by `CutPlanValidator` (`dreamglass-validator/1.3.0`) in `ValidationFailure.Code`. Any
failure disqualifies the plan (handbook §29, §31): it is never shown, approved or printed. Codes are a contract: do not
rename them; add new ones here.

| Code | Meaning |
|---|---|
| `SCHEMA` | A required part of the plan or snapshot is missing or malformed. |
| `PRECISION` | A value is finer than the snapshot's precision (`unitsPerMm`). |
| `ARITHMETIC_OVERFLOW` | A coordinate or size is outside the range the validator can check exactly. |
| `INPUT_VERSION_MISMATCH` | The plan was computed from another snapshot (different hash or job revision). |
| `UNVERIFIABLE_CLAIM` | A quality label or bound is not backed, or its arithmetic is wrong; `OptimalProven` is never accepted. |
| `UNKNOWN_SOURCE` | A source or placement refers to stock that is not in the snapshot. |
| `UNKNOWN_OFFER` | A new roll refers to a roll offer that is not in the snapshot. |
| `SOURCE_ID_MISMATCH` | A source id does not follow the convention (`stockId`, `new-roll:{offerId}`). |
| `SOURCE_DIMENSIONS` | A source has a non-positive dimension. |
| `DUPLICATE_SOURCE` | The same stock or source id appears more than once. |
| `STOCK_MISMATCH` | Stock size, pattern axis, version or material differs from the snapshot. |
| `STOCK_UNAVAILABLE` | The plan uses stock the snapshot marks as not available. |
| `ORGANIZATION_MISMATCH` | The plan uses stock of another organisation. |
| `EXCLUDED_STOCK_USED` | The plan uses stock the snapshot excluded. |
| `MULTIPLE_NEW_ROLLS` | More than one new roll in one plan. |
| `UNUSED_SOURCE` | A listed source holds no piece (untouched stock must not be committed). |
| `UNKNOWN_INSTANCE` | A placement does not correspond to a requested piece instance. |
| `DUPLICATE_INSTANCE` | A piece instance is placed twice. |
| `MISSING_INSTANCE` | A requested piece instance is not placed. |
| `PIECE_MISMATCH` | Reported piece id, sizes, padding or required orientation differ from the request. |
| `FOOTPRINT_MISMATCH` | The footprint is not the padded size in the placement's rotation. |
| `CUTOUT_SIZE_MISMATCH` | The reported cut-out size is not finished size plus padding. |
| `NOMINAL_RECT_MISMATCH` | The finished panel is not where the rotated padding puts it. |
| `TRANSFORM_INVALID` | A rotation or display transform field is inconsistent. |
| `ORIENTATION_VIOLATION` | The finished pattern direction breaks the piece's requirement, or is reported wrongly. |
| `OUT_OF_BOUNDS` | A footprint lies outside its source. |
| `OVERLAP` | Two footprints overlap. |
| `RESIDUAL_INVALID` | A leftover is malformed, duplicated, or its keep/scrap decision does not follow the remnant policy. |
| `TREE_INVALID`, `TREE_MISSING`, `TREE_ROOT_MISSING`, `TREE_ROOT_MISMATCH` | A source's cut tree is missing, duplicated or does not start from the whole source. |
| `DUPLICATE_NODE`, `TREE_CYCLE`, `TREE_DANGLING`, `TREE_ORPHAN` | The cut tree is not a tree. |
| `ILLEGAL_CUT` | A cut is not a straight edge-to-edge cut strictly inside its region. |
| `CHILD_REGION_MISMATCH` | A node's region is not what its parent's cut produces. |
| `LEAF_MISMATCH` | A leaf does not match its piece or leftover. |
| `UNCOVERED_PLACEMENT`, `UNCOVERED_RESIDUAL` | A piece or leftover is not a leaf of the cut tree. |
| `AREA_CONSERVATION` | Leaves do not exactly partition the source, or areas do not add up. |
| `CUT_STEPS_INVALID` | The numbered cut steps do not replay the tree in a physically possible order. |
| `PURCHASE_INVALID` | The purchase line differs from the offer snapshot or the recomputed layout. |
| `ORDER_LENGTH_INVALID` | The ordered length is not the smallest permitted whole-metre length covering the layout. |
| `SURPLUS_INVALID` | The full-width rounding surplus is missing, wrong, or on a non-roll source. |
| `METRIC_MISMATCH` | A reported metric differs from the value recomputed from the geometry. |
| `LEDGER_MISMATCH` | The material ledger does not balance on the reported figures. |
| `VALIDATOR_EXCEPTION` | The plan could not be read at all; it fails. |
