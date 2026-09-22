"""Synthetic development probe: Laya answers emotion questions without example prompts."""
import json
import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'inference'))
from server import EmotionWorker
worker = EmotionWorker('multilingual', 2, device='cuda')
worker.load()
if worker.status != 'ready': raise RuntimeError(worker.error)
worker.agent.cfg['head_max_len'] = 256
concepts = {'angry': ('분노, 짜증, 화남', 'anger, frustration or irritation'), 'love': ('사랑, 애정', 'love or affection'),
 'excited': ('기쁨, 행복, 신남', 'joy, happiness or excitement'), 'sad': ('슬픔, 외로움', 'sadness or loneliness'),
 'surprised': ('놀람', 'surprise'), 'sleepy': ('졸림, 피곤함', 'sleepiness or tiredness'), 'confused': ('혼란, 이해가 안 됨', 'confusion or not understanding')}
questions = {}
for label, (ko, en) in concepts.items():
 questions['ko_' + label] = {'type': 'noul', 'instructions': '글쓴이 본인이 ' + ko + '의 감정을 표현하고 있나요? 단어의 단순한 언급이 아니라 실제로 느끼는 감정을 판단하세요.'}
 questions['en_' + label] = {'type': 'noul', 'instructions': 'Does the writer express ' + en + '? Judge their actual feeling, not a word mentioned as a topic.'}
rows = {'ko': [], 'en': []}
for case in json.loads(Path('tests/emotion-cases.json').read_text(encoding='utf-8')):
 if case['split'] != 'development': continue
 answers = worker.agent.predict(case['text'], questions)['answers']
 for language in rows:
  scores = {label: answers[language + '_' + label]['noul'] for label in concepts}
  chosen = max(scores, key=scores.get)
  rows[language].append({**case, 'selected': chosen if scores[chosen] >= .7 else 'neutral', 'scores': scores})
reports = []
for name, cases in rows.items():
 report = {'language': name, 'correct': sum(r['expected'] == r['selected'] for r in cases), 'total': len(cases), 'rows': cases}
 reports.append(report)
 print(json.dumps({**{k:v for k,v in report.items() if k != 'rows'}, 'misses': [r for r in cases if r['expected'] != r['selected']]}, ensure_ascii=True), flush=True)
Path('output/verification/emotion-questions-development.json').write_text(json.dumps(reports, ensure_ascii=False, indent=2), encoding='utf-8')
