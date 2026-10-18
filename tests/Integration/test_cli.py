"""Local-only CLI regression tests; standard library, no real accounts."""
import base64, hashlib, json, os, socket, struct, subprocess, threading, time, tempfile, stat, unittest, shutil
from pathlib import Path
from datetime import datetime, timezone, timedelta

REPO = Path(__file__).resolve().parents[2]
ROOT = Path(tempfile.mkdtemp(prefix='jkcnsl-integration-'))
EXE = ['dotnet', str(REPO / 'bin/Release/net10.0/jkcnsl.dll')]

def wait(predicate, seconds=4):
    deadline=time.monotonic()+seconds
    while time.monotonic()<deadline:
        if predicate(): return True
        time.sleep(.03)
    return bool(predicate())

def write_pipe(name, data):
    deadline = time.monotonic() + 4
    while True:
        try:
            with open('\\\\.\\pipe\\' + name, 'wb', buffering=0) as pipe:
                pipe.write(data)
            return
        except OSError:
            if time.monotonic() >= deadline:
                raise
            time.sleep(.03)

class Mock:
    def __init__(self):
        self.sock=socket.socket(); self.sock.bind(('127.0.0.1',0)); self.sock.listen()
        self.port=self.sock.getsockname()[1]; self.messages=[]; self.errors=[]; self.connections=[]
        threading.Thread(target=self.accept,daemon=True).start()
    def accept(self):
        while True:
            try: conn,_=self.sock.accept()
            except OSError: return
            self.connections.append(conn)
            threading.Thread(target=self.handle,args=(conn,),daemon=True).start()
    def close(self):
        self.sock.close()
        for conn in self.connections:
            try: conn.shutdown(socket.SHUT_RDWR)
            except OSError: pass
            conn.close()
    def frame(self,conn,value,opcode=1):
        data=json.dumps(value).encode() if opcode==1 else value
        header=bytes([0x80|opcode,len(data)]) if len(data)<126 else bytes([0x80|opcode,126])+struct.pack('!H',len(data))
        conn.sendall(header+data)
    def exact(self,c,n):
        data=b''
        while len(data)<n:
            part=c.recv(n-len(data))
            if not part: raise EOFError()
            data+=part
        return data
    def recv(self,c):
        a,b=self.exact(c,2); n=b&127
        if n==126: n=struct.unpack('!H',self.exact(c,2))[0]
        elif n==127: n=struct.unpack('!Q',self.exact(c,8))[0]
        mask=self.exact(c,4) if b&128 else None
        data=self.exact(c,n)
        if mask: data=bytes(x^mask[i%4] for i,x in enumerate(data))
        return a&15,data
    def handle(self,c):
        with c:
            try:
                raw=b''
                while not raw.endswith(b'\r\n\r\n'): raw+=self.exact(c,1)
                lines=raw.decode().split('\r\n'); path=lines[0].split()[1]
                headers=dict(line.split(': ',1) for line in lines[1:] if ': ' in line)
                accept=base64.b64encode(hashlib.sha1((headers['Sec-WebSocket-Key']+'258EAFA5-E914-47DA-95CA-C5AB0DC85B11').encode()).digest()).decode()
                reply='HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: '+accept+'\r\n'
                if 'Sec-WebSocket-Protocol' in headers: reply+='Sec-WebSocket-Protocol: '+headers['Sec-WebSocket-Protocol']+'\r\n'
                c.sendall((reply+'\r\n').encode())
                op,data=self.recv(c)
                value=json.loads(data)
                self.messages.append((path,value))
                channel=path.rsplit('/',1)[1]
                now=datetime.now(timezone.utc)
                if path.startswith('/watch/'):
                    self.frame(c,{'type':'seat','data':{'keepIntervalSec':30}})
                    self.frame(c,{'type':'serverTime','data':{'currentMs':now.isoformat()}})
                    self.frame(c,{'type':'room','data':{'threadId':channel,'yourPostKey':'','vposBaseTime':(now-timedelta(hours=1)).isoformat(),'messageServer':{'uri':f'ws://127.0.0.1:{self.port}/comment/{channel}'}}})
                else:
                    self.frame(c,{'thread':{'thread':channel,'resultcode':0,'server_time':int(time.time()),'last_res':1}})
                while True:
                    op,data=self.recv(c)
                    if op==8:
                        self.frame(c,data,8); return
                    if op!=1 or not data: continue
                    value=json.loads(data); self.messages.append((path,value))
                    if isinstance(value,dict) and value.get('type')=='postComment':
                        self.frame(c,{'type':'postCommentResult','data':{}})
            except (EOFError,ConnectionError,OSError): pass
            except Exception as e: self.errors.append(str(e))

class Client:
    def __init__(self,name,settings):
        self.folder=Path(tempfile.mkdtemp(prefix=name+'-',dir=ROOT))
        (self.folder/'jkcnsl.json').write_text(json.dumps(settings))
        self.out=[]; self.err=[]
    def start(self,*args):
        self.pipe_name = args[args.index('-n') + 1] if '-n' in args else None
        self.p=subprocess.Popen(EXE+['-d',str(self.folder)]+list(args),stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8',errors='replace')
        self.t1=threading.Thread(target=lambda:self.out.extend(self.p.stdout)); self.t2=threading.Thread(target=lambda:self.err.extend(self.p.stderr)); self.t1.start(); self.t2.start()
    def send(self,line): self.p.stdin.write(line+'\n'); self.p.stdin.flush()
    def close(self):
        if self.p.poll() is None:
            if self.pipe_name: write_pipe(self.pipe_name, b'q\n')
            else: self.send('q')
        self.p.wait(timeout=12); self.t1.join(); self.t2.join()
        self.p.stdin.close(); self.p.stdout.close(); self.p.stderr.close()

class CliTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        subprocess.run(['dotnet', 'build', str(REPO / 'jkcnsl.csproj'), '-c', 'Release', '--nologo'], check=True)

    @classmethod
    def tearDownClass(cls):
        shutil.rmtree(ROOT)

    def mock(self):
        server = Mock()
        self.addCleanup(server.close)
        return server

    def client(self, settings):
        return Client(self._testMethodName, settings)

    def start(self, client, *args):
        client.start(*args)
        self.addCleanup(client.close)

    def set_fake_cookie(self, client):
        result = subprocess.run(EXE + ['-d', str(client.folder), '-c',
            'Snicovideo_cookie user_session=LOCAL_REVIEW_SENTINEL'], capture_output=True, timeout=8)
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_refuge_never_receives_login_cookie(self):
        for commentable in (False, True):
            with self.subTest(commentable=commentable):
                server = self.mock()
                client = self.client({'cache_commentable': commentable})
                self.set_fake_cookie(client)
                self.start(client)
                client.send(f'R1 ws://127.0.0.1:{server.port}/watch/jk1')
                self.assertTrue(wait(lambda: server.messages))
                self.assertNotIn('cookie', server.messages[0][1]['data'])

    def test_cache_only_receives_cookie_when_posting_is_enabled(self):
        for commentable in (False, True):
            with self.subTest(commentable=commentable):
                server = self.mock()
                client = self.client({'cache_server_url': f'ws://127.0.0.1:{server.port}',
                    'cache_commentable': commentable})
                self.set_fake_cookie(client)
                self.start(client)
                client.send('Lch123')
                self.assertTrue(wait(lambda: server.messages))
                data = server.messages[0][1]['data']
                self.assertEqual(data['room']['commentable'], commentable)
                if commentable:
                    self.assertEqual(data['cookie'], 'user_session=LOCAL_REVIEW_SENTINEL')
                else:
                    self.assertNotIn('cookie', data)

    def test_mixed_cache_does_not_leak_cookie_to_refuge(self):
        server = self.mock()
        client = self.client({'cache_server_url': f'ws://127.0.0.1:{server.port}', 'cache_commentable': True})
        self.set_fake_cookie(client)
        self.start(client)
        client.send(f'R2 ws://127.0.0.1:{server.port}/watch/jk1 ch123')
        self.assertTrue(wait(lambda: sum(path.startswith('/watch/') for path, _ in server.messages) >= 2))
        starts = {path: msg['data'] for path, msg in server.messages if path.startswith('/watch/')}
        self.assertEqual(starts['/watch/ch123']['cookie'], 'user_session=LOCAL_REVIEW_SENTINEL')
        self.assertNotIn('cookie', starts['/watch/jk1'])

    def test_blank_stdin_lines_are_ignored(self):
        client = self.client({})
        self.start(client)
        client.send('')
        client.send('S')
        self.assertTrue(wait(lambda: '.\n' in client.out))
        self.assertNotIn('?\n', client.out)

    @unittest.skipUnless(os.name == 'nt', 'Windows named pipes')
    def test_blank_pipe_lines_do_not_stop_command_processing(self):
        client = self.client({})
        name = 'jkcnsl-test-' + client.folder.name
        self.start(client, '-n', name)
        for count, newline in enumerate((b'\n', b'\r\n', b'\n', b'\r\n'), 1):
            write_pipe(name, newline)
            write_pipe(name, b'S\n')
            self.assertTrue(wait(lambda: client.out.count('.\n') == count))
            self.assertIsNone(client.p.poll())
        self.assertNotIn('!\n', client.out)

    @unittest.skipUnless(os.name == 'nt', 'Windows named pipes')
    def test_blank_pipe_lines_preserve_an_active_stream(self):
        server = self.mock()
        client = self.client({'cache_server_url': f'ws://127.0.0.1:{server.port}', 'cache_commentable': True})
        name = 'jkcnsl-test-' + client.folder.name
        self.start(client, '-n', name)
        write_pipe(name, b'Lch123\n')
        self.assertTrue(wait(lambda: any('<thread ' in line for line in client.out)))
        write_pipe(name, b'\n')
        write_pipe(name, b'\r\n')
        write_pipe(name, b'+[nico]LOCAL_ONLY_PIPE_TEST\n')
        self.assertTrue(wait(lambda: any('chat_result status="0"' in line for line in client.out)))
        self.assertIsNone(client.p.poll())

if __name__ == '__main__':
    unittest.main()
