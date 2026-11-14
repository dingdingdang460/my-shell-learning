import io, re, sys

def strip(src):
    out = []
    i = 0
    n = len(src)
    while i < n:
        c = src[i]
        if c == '"':
            i += 1
            while i < n:
                if src[i] == '\\':
                    i += 2
                    continue
                if src[i] == '"':
                    i += 1
                    break
                i += 1
            out.append('""')
        elif c == "'":
            i += 1
            while i < n:
                if src[i] == '\\':
                    i += 2
                    continue
                if src[i] == "'":
                    i += 1
                    break
                i += 1
            out.append("''")
        elif c == '/' and i + 1 < n and src[i+1] == '/':
            while i < n and src[i] != '\n':
                i += 1
        elif c == '/' and i + 1 < n and src[i+1] == '*':
            i += 2
            while i + 1 < n and not (src[i] == '*' and src[i+1] == '/'):
                i += 1
            i += 2
        else:
            out.append(c)
            i += 1
    return ''.join(out)

for path in sys.argv[1:]:
    s = io.open(path, encoding='utf-8-sig').read()
    t = strip(s)
    print(path, 'braces', t.count('{'), t.count('}'),
          'parens', t.count('('), t.count(')'),
          'brackets', t.count('['), t.count(']'),
          'OK' if (t.count('{') == t.count('}') and t.count('(') == t.count(')')
                   and t.count('[') == t.count(']')) else 'MISMATCH')
