<?php
// root/api/index.php
declare(strict_types=1);
header('Content-Type: application/json; charset=utf-8');
header('Cache-Control: no-store');
header('X-Content-Type-Options: nosniff');
header('Referrer-Policy: no-referrer');
header('X-Frame-Options: DENY');
if (!extension_loaded('json')) { http_response_code(500); exit('{"error":"JSON extension required"}'); }
$root = dirname(__DIR__, 3) . '/netforge-private';
if (!is_dir($root) && !@mkdir($root, 0700, true) && !is_dir($root)) { http_response_code(503); exit('{"error":"Private storage unavailable"}'); }
if (realpath($root) === false || str_starts_with(realpath($root), realpath(dirname(__DIR__, 2)) . DIRECTORY_SEPARATOR)) { http_response_code(503); exit('{"error":"Storage must be outside public_html"}'); }
ini_set('session.use_strict_mode', '1');
ini_set('session.cookie_httponly', '1');
ini_set('session.cookie_secure', '1');
ini_set('session.cookie_samesite', 'Strict');
session_name('NETFORGESESSID');
session_set_cookie_params(['lifetime'=>0,'path'=>'/netforge/','secure'=>true,'httponly'=>true,'samesite'=>'Strict']);
session_start();
function respond(mixed $value, int $code=200): never { http_response_code($code); echo json_encode($value, JSON_UNESCAPED_SLASHES | JSON_INVALID_UTF8_SUBSTITUTE); exit; }
function datafile(string $name): string { global $root; return $root . '/' . $name . '.json'; }
function locked(string $name, callable $fn): mixed {
    global $root;
    $lock = fopen($root . '/' . $name . '.lock', 'c');
    if (!$lock || !flock($lock, LOCK_EX)) respond(['error'=>'Storage lock failed'],503);
    try {
        $path = datafile($name);
        $raw = is_file($path) ? file_get_contents($path) : false;
        $data = $raw === false ? [] : json_decode($raw, true, 512, JSON_THROW_ON_ERROR);
        if (!is_array($data)) throw new RuntimeException('Invalid storage format');
        return $fn($data, $path);
    } finally { flock($lock, LOCK_UN); fclose($lock); }
}
function savejson(string $path, array $data): void {
    $tmp = tempnam(dirname($path), '.netforge-');
    if ($tmp === false) throw new RuntimeException('Temporary file creation failed');
    try {
        chmod($tmp, 0600);
        $json = json_encode($data, JSON_PRETTY_PRINT | JSON_UNESCAPED_SLASHES | JSON_THROW_ON_ERROR);
        if (file_put_contents($tmp, $json, LOCK_EX) === false || !rename($tmp, $path)) throw new RuntimeException('Storage write failed');
    } finally { if (is_file($tmp)) unlink($tmp); }
}
function body(): array { $raw = file_get_contents('php://input'); if (strlen($raw)>65536) respond(['error'=>'Payload too large'],413); $value=json_decode($raw,true); if (!is_array($value)) respond(['error'=>'Invalid JSON body'],400); return $value; }
function csrf(): void { if (!hash_equals((string)($_SESSION['csrf']??''),(string)($_SERVER['HTTP_X_CSRF_TOKEN']??''))) respond(['error'=>'Invalid CSRF token'],403); }
function audit(string $action, string $entity, string $id): void { locked('audit',function($rows,$path)use($action,$entity,$id){ $rows[]=['time'=>gmdate('c'),'user'=>$_SESSION['username']??'unknown','action'=>$action,'entity'=>$entity,'id'=>$id]; if(count($rows)>10000) $rows=array_slice($rows,-10000); savejson($path,$rows); }); }
try {
    $method=$_SERVER['REQUEST_METHOD']??'GET';
    $action=$_GET['action']??'';
    if ($method==='GET' && $action==='status') {
        $installed=is_file(datafile('auth'));
        respond(['installed'=>$installed,'authenticated'=>!empty($_SESSION['user']),'username'=>$_SESSION['username']??null,'csrf'=>$_SESSION['csrf']??null]);
    }
    if ($method==='POST' && $action==='setup') {
        if (is_file(datafile('auth'))) respond(['error'=>'Already configured'],409);
        $b=body(); $user=trim((string)($b['username']??'')); $pass=(string)($b['password']??'');
        if (!preg_match('/^[a-zA-Z0-9_.-]{3,40}$/',$user) || strlen($pass)<14) respond(['error'=>'Username must be 3-40 valid characters and password at least 14 characters'],422);
        $handle=@fopen(datafile('auth'),'x');
        if (!$handle) respond(['error'=>'Already configured or cannot initialize'],409);
        $auth=['username'=>$user,'hash'=>password_hash($pass,PASSWORD_DEFAULT)];
        fwrite($handle,json_encode($auth,JSON_THROW_ON_ERROR)); fclose($handle); chmod(datafile('auth'),0600);
        session_regenerate_id(true); $_SESSION['user']=true; $_SESSION['username']=$user; $_SESSION['csrf']=bin2hex(random_bytes(32));
        respond(['ok'=>true,'csrf'=>$_SESSION['csrf']],201);
    }
    if ($method==='POST' && $action==='login') {
        $b=body(); $auth=json_decode((string)@file_get_contents(datafile('auth')),true);
        if (!is_array($auth) || !hash_equals($auth['username'],(string)($b['username']??'')) || !password_verify((string)($b['password']??''),$auth['hash'])) { usleep(350000); respond(['error'=>'Invalid credentials'],401); }
        session_regenerate_id(true); $_SESSION['user']=true; $_SESSION['username']=$auth['username']; $_SESSION['csrf']=bin2hex(random_bytes(32));
        respond(['ok'=>true,'csrf'=>$_SESSION['csrf']]);
    }
    if (empty($_SESSION['user'])) respond(['error'=>'Authentication required'],401);
    if ($method==='POST' && $action==='logout') { csrf(); $_SESSION=[]; session_destroy(); respond(['ok'=>true]); }
    $allowed=['customers','highsites','sectors','equipment','backhauls','scripts','jobs'];
    $category=(string)($_GET['category']??'');
    if (!in_array($category,$allowed,true)) respond(['error'=>'Unknown category'],404);
    if ($method==='GET') respond(locked($category,fn($rows)=>$rows));
    csrf();
    if ($method==='POST') {
        $b=body();
        $name=trim((string)($b['name']??''));
        if ($name==='' || strlen($name)>160) respond(['error'=>'Name is required (max 160 bytes)'],422);
        $item=['id'=>bin2hex(random_bytes(16)),'name'=>$name,'description'=>substr(trim((string)($b['description']??'')),0,2000),'created_at'=>gmdate('c'),'updated_at'=>gmdate('c')];
        if ($category==='sectors' || $category==='equipment' || $category==='backhauls') $item['highsite_id']=trim((string)($b['highsite_id']??''));
        if ($category==='customers') $item['sector_id']=trim((string)($b['sector_id']??''));
        if ($category==='jobs') respond(['error'=>'Job execution is disabled until a secure agent is connected'],403);
        locked($category,function($rows,$path)use($item){ $rows[]=$item; savejson($path,$rows); });
        audit('create',$category,$item['id']); respond($item,201);
    }
    if ($method==='DELETE') {
        $id=(string)($_GET['id']??''); if (!preg_match('/^[a-f0-9]{32}$/',$id)) respond(['error'=>'Invalid ID'],422);
        if ($category==='jobs') respond(['error'=>'Job execution is disabled'],403);
        $deleted=locked($category,function($rows,$path)use($id){ $next=array_values(array_filter($rows,fn($r)=>($r['id']??'')!==$id)); if(count($next)===count($rows))return false; savejson($path,$next); return true; });
        if (!$deleted) respond(['error'=>'Not found'],404);
        audit('delete',$category,$id); respond(['ok'=>true]);
    }
    respond(['error'=>'Method not allowed'],405);
} catch (Throwable $e) { error_log('NetForge API: '.$e->getMessage()); respond(['error'=>'Internal server error'],500); }
