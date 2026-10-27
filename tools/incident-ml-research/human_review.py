"""Read human workbook verdicts without executing formulas or changing the source."""
import argparse
import csv
import hashlib
import json
import math
import posixpath
import re
from pathlib import Path
from zipfile import ZipFile
import xml.etree.ElementTree as ET

NS = {'s': 'http://schemas.openxmlformats.org/spreadsheetml/2006/main'}
REL = 'http://schemas.openxmlformats.org/officeDocument/2006/relationships'


def normalize_decision(value):
    key = str(value or '').strip().casefold()
    options = {'tak': 'yes', 'yes': 'yes', 'nie': 'no', 'no': 'no',
               'nie wiem': 'unknown', 'unknown': 'unknown', '': 'unknown'}
    if key not in options:
        raise ValueError(f'Unrecognized human verdict: {value!r}')
    return options[key]


def sector_numbers(value):
    if value is None or str(value).strip().casefold() in ['', 'nie wiem', 'unknown']:
        return []
    try:
        number = float(value)
        numbers = [int(number)] if number.is_integer() else [-1]
    except ValueError:
        numbers = [int(n) for n in re.findall(r'\d+', str(value))]
    if not numbers or any(n < 1 or n > 5 for n in numbers):
        raise ValueError(f'Invalid reviewed sector(s): {value!r}')
    return sorted(set(numbers))


def evidence_flags(decision, sectors, note):
    text = (note or '').casefold()
    mechanism = re.search(r'upad|kraks|gleb|flak|awari|urw|crash|off.course', text)
    outcome = ((decision == 'yes' and 'dnf' in text and not sectors and not mechanism) or
               (decision == 'no' and re.search(r'wygr|winner|pierwsz', text) and
                not re.search(r'potwierdz|czyst|ogląda|video', text)))
    return ['outcome_only'] if outcome else []


def workbook_cells(path):
    with ZipFile(path) as archive:
        if sum(i.file_size for i in archive.infolist()) > 100_000_000:
            raise ValueError('Review workbook exceeds extraction size limit')
        workbook = ET.fromstring(archive.read('xl/workbook.xml'))
        relations = {r.attrib['Id']: r.attrib for r in
                     ET.fromstring(archive.read('xl/_rels/workbook.xml.rels'))}
        shared = []
        if 'xl/sharedStrings.xml' in archive.namelist():
            for item in ET.fromstring(archive.read('xl/sharedStrings.xml')).findall('s:si', NS):
                shared.append(''.join(t.text or '' for t in item.iter('{'+NS['s']+'}t')))
        sheets = {}
        for sheet in workbook.findall('s:sheets/s:sheet', NS):
            relation = relations[sheet.attrib['{'+REL+'}id']]
            if relation.get('TargetMode') == 'External':
                raise ValueError('External worksheet references are not accepted')
            target = relation['Target']
            member = target.lstrip('/') if target.startswith('/') else posixpath.normpath('xl/'+target)
            if not member.startswith('xl/worksheets/'):
                raise ValueError('Unexpected worksheet target')
            values, formulas = {}, set()
            for cell in ET.fromstring(archive.read(member)).findall('s:sheetData/s:row/s:c', NS):
                address = cell.attrib['r']
                text = cell.findtext('s:v', default='', namespaces=NS)
                if cell.attrib.get('t') == 's':
                    text = shared[int(text)] if text else ''
                elif cell.attrib.get('t') == 'inlineStr':
                    text = ''.join(t.text or '' for t in cell.findall('.//s:t', NS))
                values[address] = text
                if cell.find('s:f', NS) is not None:
                    formulas.add(address)
            sheets[sheet.attrib['name']] = values, formulas
        return sheets


def extract(path, source):
    sheets = workbook_cells(path)
    cells, formulas = sheets['Przypadki']
    with source.open(encoding='utf-8-sig', newline='') as handle:
        original = {r['reviewId']: r for r in csv.DictReader(handle)}
    starts = sorted((int(re.search(r'\d+', address).group()), match.group(1))
                    for address, value in cells.items() if address.startswith('B') and
                    (match := re.fullmatch(r'Przypadek\s+(\d+)', value, re.I)))
    if len(starts) != len(original) or {key for _, key in starts} != set(original):
        raise ValueError('Review IDs are missing, duplicated or changed')
    result = []
    for index, (start, key) in enumerate(starts):
        stop = starts[index+1][0] if index+1 < len(starts) else start+20
        answer_rows = [r for r in range(start, stop) if cells.get(f'B{r}') == 'Incydent?']
        note_rows = [r for r in range(start, stop) if cells.get(f'B{r}') == 'Źródło / uwagi']
        if len(answer_rows) != 1 or len(note_rows) != 1:
            raise ValueError(f'Cannot locate response fields for review {key}')
        answer, note_row = answer_rows[0], note_rows[0]
        addresses = [f'C{answer}', f'E{answer}', f'C{note_row}']
        if any(a in formulas for a in addresses):
            raise ValueError(f'Human response replaced by a formula for review {key}')
        row = original[key]
        if cells.get(f'C{start}', '').strip().casefold() != row['rider'].strip().casefold():
            raise ValueError(f'Rider header changed for review {key}')
        for sector in range(1, 6):
            r = start+2+sector
            for column, name in [('C', f'sector{sector}'), ('D', f'best{sector}'), ('G', f'sectorRank{sector}')]:
                actual, expected = cells.get(f'{column}{r}', ''), row[name]
                if actual == '' and expected == '':
                    continue
                if actual == '' or expected == '' or not math.isclose(float(actual), float(expected), abs_tol=1e-7, rel_tol=1e-9):
                    raise ValueError(f'Displayed measurement changed: review {key}, {name}')
        decision = normalize_decision(cells.get(addresses[0], ''))
        sectors = sector_numbers(cells.get(addresses[1], ''))
        note = cells.get(addresses[2], '').strip()
        result.append(dict(review_id=key, decision=decision, sectors=sectors,
            evidence_flags=evidence_flags(decision, sectors, note),
            note=note, source_cells=addresses,
            year=int(row['year']), rider=row['rider'], venue=row['venue'], category=row['category'],
            session=row['session'], status=row['status'],
            times=[float(row[f'sector{j}']) if row[f'sector{j}'] else None for j in range(1, 6)]))
    return result


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--workbook', type=Path, required=True)
    parser.add_argument('--source', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    digest = hashlib.sha256(args.workbook.read_bytes()).hexdigest()
    rows = extract(args.workbook, args.source)
    assert hashlib.sha256(args.workbook.read_bytes()).hexdigest() == digest
    payload = dict(workbook_sha256=digest, source_csv_sha256=hashlib.sha256(args.source.read_bytes()).hexdigest(),
        source_unchanged=True, source_measurements_verified=True, rows=rows)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(payload, indent=2, ensure_ascii=False), encoding='utf-8')
    print(json.dumps(dict(cases=len(rows), decisions={d: sum(r['decision'] == d for r in rows) for d in ['yes', 'no', 'unknown']},
        sector_annotated=sum(bool(r['sectors']) for r in rows),
        outcome_only=[r['review_id'] for r in rows if 'outcome_only' in r['evidence_flags']],
        source_unchanged=True), indent=2))


if __name__ == '__main__':
    main()
