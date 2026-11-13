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
        $matched=is_array($auth)&&hash_equals($auth['username'],(string)($b['username']??''))&&password_verify((string)($b['password']??''),$auth['hash']);
        if(!$matched){$staff=locked('staff',fn($r)=>$r);foreach($staff as $member){if(hash_equals((string)($member['username']??''),(string)($b['username']??''))&&password_verify((string)($b['password']??''),(string)($member['password_hash']??''))){$matched=true;$auth=['username'=>$member['username']];break;}}}
        if(!$matched){usleep(350000);respond(['error'=>'Invalid credentials'],401);}
        session_regenerate_id(true); $_SESSION['user']=true; $_SESSION['username']=$auth['username']; $_SESSION['csrf']=bin2hex(random_bytes(32));
        respond(['ok'=>true,'csrf'=>$_SESSION['csrf']]);
    }
    if (empty($_SESSION['user'])) respond(['error'=>'Authentication required'],401);
    if ($method==='POST' && $action==='logout') { csrf(); $_SESSION=[]; session_destroy(); respond(['ok'=>true]); }
    $allowed=['customers','highsites','sectors','equipment','backhauls','scripts','jobs','tickets','branches','technicians','packages','staff'];
    $category=(string)($_GET['category']??'');
    if (!in_array($category,$allowed,true)) respond(['error'=>'Unknown category'],404);
    if ($method==='GET') respond(locked($category,function($rows)use($category){if($category==='staff')return array_map(fn($x)=>['id'=>$x['id'],'name'=>$x['name']??'','username'=>$x['username']??'','role'=>$x['role']??'viewer','created_at'=>$x['created_at']??''],$rows);return $rows;}));
    csrf();
    if ($method==='POST' || $method==='PUT') {
        $b=body();
        if ($category==='jobs') respond(['error'=>'Script execution requires an external agent'],403);
        $name=trim((string)($b['name']??$b['title']??$b['account']??''));
        if ($name==='' || strlen($name)>160) respond(['error'=>'Name/account/title required (max 160 bytes)'],422);
        $fields=[
          'customers'=>['account','name','surname','address','phone','email','package','branch_id','sector_id','latitude','longitude','cpe_ip','cpe_username','notes'],
          'tickets'=>['name','number','type','status','priority','customer_id','highsite_id','branch_id','technician_id','scheduled_date','description','call_log'],
          'highsites'=>['name','number','address','branch_id','latitude','longitude','notes'],
          'equipment'=>['name','type','category','highsite_id','customer_id','ip','username','protocol','port','notes'],
          'branches'=>['name','address','phone'],
          'technicians'=>['name','surname','branch_id','phone','active'],
          'scripts'=>['name','language','description','body','risk'],
          'staff'=>['name','username','role'],
          'sectors'=>['name','highsite_id','azimuth','frequency','notes'],
          'backhauls'=>['name','highsite_id','remote_highsite_id','frequency','notes'],
          'packages'=>['name','speed','price'],
        ];
        $item=[];
        foreach(($fields[$category]??['name','description']) as $field){
          $value=$b[$field]??'';
          if(!is_scalar($value)) respond(['error'=>'Invalid field '.$field],422);
          $item[$field]=substr(trim((string)$value),0,in_array($field,['description','notes','call_log'],true)?4000:($field==='body'?16000:300));
        }
        if($category==='tickets'&&!in_array($item['type'],['customer','highsite','voip','other'],true))respond(['error'=>'Invalid ticket type'],422);
        if($category==='equipment'&&!in_array($item['category'],['distribution','backhaul','access','power','cpe'],true))respond(['error'=>'Invalid equipment category'],422);
        if($category==='staff'){
          if($method==='PUT')respond(['error'=>'Staff updates not yet supported'],405);
          if(!in_array($item['role'],['admin','dispatcher','technician','viewer'],true))respond(['error'=>'Invalid role'],422);
          $password=(string)($b['password']??'');
          if(strlen($password)<14)respond(['error'=>'Staff password must be at least 14 characters'],422);
          $item['password_hash']=password_hash($password,PASSWORD_DEFAULT);
        }
        if($method==='PUT'){
          $id=(string)($b['id']??'');if(!preg_match('/^[a-f0-9]{32}$/',$id))respond(['error'=>'Invalid ID'],422);
          $item['id']=$id;$item['updated_at']=gmdate('c');
          $saved=locked($category,function($rows,$path)use($item,$id){$found=false;foreach($rows as &$row){if(($row['id']??'')===$id){$item['created_at']=$row['created_at']??gmdate('c');$row=$item;$found=true;break;}}unset($row);if(!$found)respond(['error'=>'Not found'],404);savejson($path,$rows);return $item;});
          audit('update',$category,$id);respond($saved);
        }
        $item['id']=bin2hex(random_bytes(16));$item['created_at']=gmdate('c');$item['updated_at']=$item['created_at'];
        locked($category,function($rows,$path)use($item,$category){if($category==='staff'){foreach($rows as $r){if(strcasecmp($r['username']??'',$item['username'])===0)respond(['error'=>'Username exists'],409);}}$rows[]=$item;savejson($path,$rows);});
        audit('create',$category,$item['id']);
        if($category==='staff')unset($item['password_hash']);
        respond($item,201);
    }
    if ($method==='DELETE') {
        $id=(string)($_GET['id']??'');if (!preg_match('/^[a-f0-9]{32}$/',$id)) respond(['error'=>'Invalid ID'],422);
        if ($category==='jobs') respond(['error'=>'Job execution is disabled'],403);
        $deleted=locked($category,function($rows,$path)use($id){$next=array_values(array_filter($rows,fn($r)=>($r['id']??'')!==$id));if(count($next)===count($rows))return false;savejson($path,$next);return true;});
        if (!$deleted) respond(['error'=>'Not found'],404);
        audit('delete',$category,$id);respond(['ok'=>true]);
    }
    respond(['error'=>'Method not allowed'],405);
} catch (Throwable $e) { error_log('NetForge API: '.$e->getMessage()); respond(['error'=>'Internal server error'],500); }
