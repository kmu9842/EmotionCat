"""Benchmark synthetic phrases against an already-owned worker; never read typing."""
import json
import os
import statistics
import time
import urllib.request
from pathlib import Path

settings = json.loads((Path(os.environ['LOCALAPPDATA']) / 'EmotionCat/settings.json').read_text(encoding='utf-8-sig'))
labels = [{'id': e['Id'], 'name': e['Name'], 'description': e['Description']} for e in settings['Emotions']]
headers = {'Content-Type': 'application/json', 'X-EmotionCat-Token': os.environ['EMOTIONCAT_TEST_TOKEN']}
def call(endpoint, data=None):
    body = None if data is None else json.dumps(data).encode('utf-8')
    req = urllib.request.Request('http://127.0.0.1:47821/' + endpoint, data=body, headers=headers)
    with urllib.request.urlopen(req, timeout=20) as reply:
        return json.load(reply)

before = call('health')
rows = []
samples = [('excited', '와 신난다! 너무 행복하고 기뻐!'),
           ('love', '너무 사랑해! 정말 좋아하고 아껴!'),
           ('sad', '너무 슬프고 외로워. 눈물이 나.'),
           ('angry', '제대로 좀 해라 진짜 짜증나게 하네')]
for i in range(28):
    expected, text = samples[i % len(samples)]
    start = time.perf_counter()
    result = call('classify', {'text': text, 'emotions': labels,
        'instructions': settings['ClassificationPrompt'], 'source': 'test'})
    elapsed = (time.perf_counter() - start) * 1000
    rows.append({'expected': expected, 'selected': result['emotion'],
        'confidence': result['confidence'], 'model_ms': result['elapsed_ms'], 'roundtrip_ms': round(elapsed, 1)})
    time.sleep(max(0, .35 - elapsed / 1000))
after = call('health')
report = {'samples': len(rows), 'cadence_ms': 350, 'device': after['device'],
          'model_ms_median': round(statistics.median(r['model_ms'] for r in rows), 1),
          'model_ms_min': min(r['model_ms'] for r in rows), 'model_ms_max': max(r['model_ms'] for r in rows),
          'roundtrip_ms_median': round(statistics.median(r['roundtrip_ms'] for r in rows), 1),
          'gpu_allocated_mib': after['gpu_allocated_mb'], 'gpu_reserved_mib': after['gpu_reserved_mb'],
          'startup_peak_allocated_mib': before['gpu_peak_mb'], 'rows': rows}
Path('output/verification/production-gpu-benchmark.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
print(json.dumps({k:v for k,v in report.items() if k != 'rows'}))
print('Synthetic label matches: %d/%d' % (sum(r['expected'] == r['selected'] for r in rows), len(rows)))
