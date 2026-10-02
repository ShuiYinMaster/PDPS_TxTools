"""Isolated Python entry point: explicitly add only our deployed decoder folder."""
import sys
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parent))
from jt_direct_candidate import convert

if __name__=='__main__':
    try:
        if sys.version_info<(3,10): raise ValueError('JT decoder requires Python 3.10 or later')
        if len(sys.argv)!=3: raise ValueError('Usage: jt_direct_entry.py source.jt output.jtmesh')
        convert(Path(sys.argv[1]),Path(sys.argv[2]))
    except Exception as exc:
        print(f'{type(exc).__name__}: {exc}',file=sys.stderr)
        sys.exit(1)
