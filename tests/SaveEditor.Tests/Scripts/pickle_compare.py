"""Structural comparator for two Python pickle streams.

Ren'Py saves reference game-specific classes (renpy.python.RevertableDict,
store.InventoryItem, ...) that aren't importable outside the game. Rather
than trying to import them, every GLOBAL/STACK_GLOBAL lookup is resolved to
a generic stand-in class that just records what pickle did with it (args,
state, appended items, set items). Two pickle streams are then considered
equivalent if their generic object graphs are deeply equal, independent of
incidental differences in opcode choice or memoization strategy.
"""
import pickle
import sys

_class_cache: dict[tuple[str, str], type] = {}


def _get_marker_class(module: str, name: str) -> type:
    key = (module, name)
    cls = _class_cache.get(key)
    if cls is not None:
        return cls

    def __new__(cls, *args, **kwargs):
        inst = object.__new__(cls)
        inst._gi_args = args
        inst._gi_kwargs = kwargs
        inst._gi_state = None
        inst._gi_has_state = False
        inst._gi_appends = None
        inst._gi_setitems = None
        return inst

    def __init__(self, *args, **kwargs):
        pass  # construction is fully handled in __new__

    def __setstate__(self, state):
        self._gi_has_state = True
        self._gi_state = state

    def append(self, item):
        if self._gi_appends is None:
            self._gi_appends = []
        self._gi_appends.append(item)

    def extend(self, items):
        if self._gi_appends is None:
            self._gi_appends = []
        self._gi_appends.extend(items)

    def __setitem__(self, k, v):
        if self._gi_setitems is None:
            self._gi_setitems = {}
        self._gi_setitems[k] = v

    def __repr__(self):
        return f"<{module}.{name} args={self._gi_args!r}>"

    cls = type(
        f"GI_{len(_class_cache)}",
        (),
        {
            "__new__": __new__,
            "__init__": __init__,
            "__setstate__": __setstate__,
            "append": append,
            "extend": extend,
            "__setitem__": __setitem__,
            "__repr__": __repr__,
            "_gi_module": module,
            "_gi_name": name,
        },
    )
    _class_cache[key] = cls
    return cls


class GenericUnpickler(pickle.Unpickler):
    def find_class(self, module, name):
        return _get_marker_class(module, name)

    def persistent_load(self, pid):
        return ("__persid__", pid)


def load_generic(path):
    with open(path, "rb") as f:
        return GenericUnpickler(f).load()


def _is_generic(v):
    return hasattr(v, "_gi_module")


def deep_eq(a, b, seen) -> bool:
    if a is b:
        return True
    key = (id(a), id(b))
    if key in seen:
        return True  # already being compared further up the call stack (cycle)

    if _is_generic(a) and _is_generic(b):
        if (a._gi_module, a._gi_name) != (b._gi_module, b._gi_name):
            return False
        seen.add(key)
        return (
            deep_eq(a._gi_args, b._gi_args, seen)
            and deep_eq(a._gi_kwargs, b._gi_kwargs, seen)
            and a._gi_has_state == b._gi_has_state
            and deep_eq(a._gi_state, b._gi_state, seen)
            and deep_eq(a._gi_appends, b._gi_appends, seen)
            and deep_eq(a._gi_setitems, b._gi_setitems, seen)
        )
    if _is_generic(a) or _is_generic(b):
        return False

    if isinstance(a, dict) and isinstance(b, dict):
        if len(a) != len(b):
            return False
        seen.add(key)
        for (ka, va), (kb, vb) in zip(a.items(), b.items()):
            if not deep_eq(ka, kb, seen) or not deep_eq(va, vb, seen):
                return False
        return True

    if isinstance(a, (list, tuple)) and isinstance(b, (list, tuple)):
        if type(a) is not type(b) or len(a) != len(b):
            return False
        seen.add(key)
        return all(deep_eq(x, y, seen) for x, y in zip(a, b))

    if isinstance(a, (set, frozenset)) and isinstance(b, (set, frozenset)):
        if type(a) is not type(b) or len(a) != len(b):
            return False
        seen.add(key)
        remaining = list(b)
        for xa in a:
            for i, xb in enumerate(remaining):
                if deep_eq(xa, xb, set(seen)):
                    del remaining[i]
                    break
            else:
                return False
        return True

    if isinstance(a, bool) or isinstance(b, bool):
        return type(a) is type(b) and a == b

    if isinstance(a, (int, float)) and isinstance(b, (int, float)):
        return type(a) is type(b) and a == b

    return type(a) is type(b) and a == b


def main():
    a = load_generic(sys.argv[1])
    b = load_generic(sys.argv[2])
    if deep_eq(a, b, set()):
        print("MATCH")
        return 0
    print("MISMATCH")
    return 1


if __name__ == "__main__":
    sys.exit(main())
