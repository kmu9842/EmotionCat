"""Compare frozen development phrases. Validation is a separate invocation."""
import argparse
import json
import os
from pathlib import Path
import urllib.request

parser = argparse.ArgumentParser()
parser.add_argument('--split', choices=['development', 'validation'], default='development')
parser.add_argument('--candidate', default='all')
args = parser.parse_args()
settings = json.loads((Path(os.environ['LOCALAPPDATA']) / 'EmotionCat/settings.json').read_text(encoding='utf-8-sig'))
labels = ['neutral', 'angry', 'love', 'excited', 'sad', 'surprised', 'sleepy', 'confused']
korean = ['평온, 특별한 감정 없는 일상 대화나 정보 전달', '화남, 짜증, 분노, 불만', '사랑, 애정, 호감, 고마움', '신남, 기쁨, 행복, 즐거움', '슬픔, 우울, 외로움, 상실', '놀람, 예상치 못한 일에 깜짝 놀람', '졸림, 피곤함, 잠이 옴', '혼란, 이해가 안 됨, 어리둥절함']
names = ['평온', '화남', '사랑', '기쁨', '슬픔', '놀람', '졸림', '혼란']
ko_prompt = '이 문장에서 글쓴이가 표현하는 감정은 무엇인가요? 감정이 드러나지 않는 정보나 평범한 질문은 neutral을 선택하세요.'
base = [{'id': e['Id'], 'name': e['Name'], 'description': e['Description']} for e in settings['Emotions']]
ko = [{'id': label, 'name': names[i], 'description': korean[i]} for i, label in enumerate(labels)]
short = [{'id': label, 'name': names[i], 'description': names[i]} for i, label in enumerate(labels)]
candidates = {
 'current': (settings['ClassificationPrompt'], base),
 'reverse': (settings['ClassificationPrompt'], list(reversed(base))),
 'question': ('문장을 쓴 사람은 어떤 감정을 느끼고 있나요?', base),
 'ko': (ko_prompt, ko),
 'ko_short': ('글쓴이의 현재 감정을 하나 선택하세요. 감정 표현이 없으면 평온입니다.', short),
 'ko_direct': ('문장의 감정을 분류하세요. 불평과 욕설은 화남, 애정 표현은 사랑, 긍정적인 즐거움은 기쁨입니다. 감정이 없는 질문이나 정보는 평온입니다.', ko),
}
cases = [r for r in json.loads(Path('tests/emotion-cases.json').read_text(encoding='utf-8')) if r['split'] == args.split]
reports = []
for name, (prompt, emotions) in candidates.items():
 if args.candidate != 'all' and name != args.candidate: continue
 rows = []
 for case in cases:
  body = json.dumps({'text': case['text'], 'emotions': emotions, 'instructions': prompt, 'source': 'test'}).encode()
  req = urllib.request.Request('http://127.0.0.1:47821/classify', data=body,
      headers={'Content-Type': 'application/json', 'X-EmotionCat-Token': os.environ['EMOTIONCAT_TEST_TOKEN']})
  with urllib.request.urlopen(req, timeout=20) as response: result = json.load(response)
  selected = result['emotion'] if result['confidence'] >= settings['MinConfidence'] else 'neutral'
  rows.append({**case, 'raw': result['emotion'], 'selected': selected, 'confidence': result['confidence'], 'probabilities': result['probabilities'], 'ms': result['elapsed_ms']})
 report = {'candidate': name, 'split': args.split, 'correct': sum(r['expected'] == r['selected'] for r in rows), 'total': len(rows), 'rows': rows}
 reports.append(report)
 print(json.dumps({**{k:v for k,v in report.items() if k != 'rows'}, 'misses': [{k:v for k,v in r.items() if k != 'probabilities'} for r in rows if r['selected'] != r['expected']]}, ensure_ascii=False), flush=True)
Path('output/verification/emotion-' + args.split + '-' + args.candidate + '.json').write_text(json.dumps(reports, ensure_ascii=False, indent=2), encoding='utf-8')
