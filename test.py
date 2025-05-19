from datetime import datetime, timedelta, timezone
from google_auth_oauthlib.flow import InstalledAppFlow
from googleapiclient.discovery import build
import json

SCOPES = ['https://www.googleapis.com/auth/calendar.readonly']

def main():
    # OAuth認証
    flow = InstalledAppFlow.from_client_secrets_file('credentials.json', SCOPES)
    creds = flow.run_local_server(port=0)
    service = build('calendar', 'v3', credentials=creds)

    # 今日の開始・終了（日本時間 → UTC）
    JST = timezone(timedelta(hours=9))
    start = datetime.now(JST).replace(hour=0, minute=0, second=0, microsecond=0).astimezone(timezone.utc).isoformat()
    end = datetime.now(JST).replace(hour=23, minute=59, second=59, microsecond=0).astimezone(timezone.utc).isoformat()

    # カレンダー一覧取得
    calendar_list = service.calendarList().list().execute()
    for cal in calendar_list.get('items', []):
        print(json.dumps(cal, indent=2, ensure_ascii=False))
    print("=== 今日の予定 ===")

    for cal in calendar_list.get('items', []):
        cal_id = cal['id']
        cal_name = cal['summary']

        # 各カレンダーの予定取得
        events_result = service.events().list(
            calendarId=cal_id,
            timeMin=start,
            timeMax=end,
            singleEvents=True,
            orderBy='startTime'
        ).execute()

        events = events_result.get('items', [])
        if events:
            print(f"\n📅 カレンダー: {cal_name}")
            for event in events:
                start_time = event['start'].get('dateTime', event['start'].get('date'))
                summary = event.get('summary', 'No Title')
                print(f"🕒 {start_time} - {summary}")

if __name__ == '__main__':
    main()
