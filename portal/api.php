<?php
// root/portal/api.php
declare(strict_types=1);

// The subscriber portal has its own cookie and authorization boundary.
// It NEVER accepts a customer_id from a request to select whose data is read.
header('Content-Type: application/json; charset=utf-8');
header('Cache-Control: no-store');
header('X-Content-Type-Options: nosniff');
header('Referrer-Policy: no-referrer');
header('X-Frame-Options: DENY');
$root=dirname(__DIR__,3).'/netforge-private';
$public=realpath(dirname(__DIR__,2));
if(!is_dir($root)||!($real=realpath($root))||!$public||str_starts_with($real,$public.DIRECTORY_SEPARATOR)){
  http_response_code(503);exit('{"error":"Private storage unavailable"}');
}
ini_set('session.use_strict_mode','1');
session_name('NFPORTALSESSID');
session_set_cookie_params(['lifetime'=>0,'path'=>'/netforge/portal/','secure'=>true,'httponly'=>true,'samesite'=>'Strict']);
session_start();

function result(mixed $data,int $status=200): never {
  http_response_code($status);echo json_encode($data,JSON_UNESCAPED_SLASHES|JSON_INVALID_UTF8_SUBSTITUTE);exit;
}
function portalRows(string $name,callable $callback): mixed {
  global $root;
  if(!preg_match('/^[a-z_]{3,40}$/',$name))throw new RuntimeException('Invalid collection');
  $lock=@fopen($root.'/'.$name.'.lock','c');
  if(!$lock||!flock($lock,LOCK_EX))result(['error'=>'Storage unavailable'],503);
  try {
    $file=$root.'/'.$name.'.json';
    $raw=is_file($file)?file_get_contents($file):false;
    $rows=$raw===false?[]:json_decode($raw,true,512,JSON_THROW_ON_ERROR);
    if(!is_array($rows))throw new RuntimeException('Invalid data store');
    return $callback($rows,$file);
  } finally {flock($lock,LOCK_UN);fclose($lock);}
}
function portalSave(string $path,array $data): void {
  $temp=tempnam(dirname($path),'.nfportal-');
  if($temp===false)throw new RuntimeException('Temporary file unavailable');
  try{
    chmod($temp,0600);
    $json=json_encode($data,JSON_PRETTY_PRINT|JSON_UNESCAPED_SLASHES|JSON_THROW_ON_ERROR);
    if(file_put_contents($temp,$json,LOCK_EX)===false||!rename($temp,$path))
      throw new RuntimeException('Write failed');
  } finally {if(is_file($temp))@unlink($temp);}
}
function portalBody(): array {
  $raw=file_get_contents('php://input');
  if(strlen($raw)>16384)result(['error'=>'Payload too large'],413);
  $data=json_decode($raw,true);
  if(!is_array($data))result(['error'=>'Invalid JSON'],400);
  return $data;
}
function portalCsrf(): void {
  if(!hash_equals((string)($_SESSION['csrf']??''),(string)($_SERVER['HTTP_X_CSRF_TOKEN']??'')))
    result(['error'=>'CSRF validation failed'],403);
}
function portalThrottle(string $username,string $event): bool {
  $key=hash('sha256',(string)($_SERVER['REMOTE_ADDR']??'unknown').'|'.strtolower($username));
  return portalRows('portal_login_attempts',function($rows,$path)use($key,$event){
    $now=time();$rows=array_values(array_filter($rows,fn($x)=>($x['time']??0)>$now-900));
    $count=count(array_filter($rows,fn($x)=>($x['key']??'')===$key));
    if($event==='failure'){$rows[]=['key'=>$key,'time'=>$now];if(count($rows)>15000)$rows=array_slice($rows,-15000);portalSave($path,$rows);}
    if($event==='success'){$rows=array_values(array_filter($rows,fn($x)=>($x['key']??'')!==$key));portalSave($path,$rows);}
    return $count>=8;
  });
}
function portalAudit(string $event,string $customerId): void {
  portalRows('portal_audit',function($rows,$path)use($event,$customerId){
    $rows[]=['time'=>gmdate('c'),'customer_id'=>$customerId,'event'=>$event];
    if(count($rows)>10000)$rows=array_slice($rows,-10000);
    portalSave($path,$rows);
  });
}
function safeRows(string $name,string $customerId,array $fields,bool $onlyPublic=false): array {
  $rows=portalRows($name,fn($r)=>$r);
  return array_values(array_map(function($r)use($fields){
    return array_intersect_key($r,array_flip($fields));
  },array_filter($rows,function($r)use($customerId,$onlyPublic){
    return ($r['customer_id']??'')===$customerId&&(!$onlyPublic||!in_array($r['status']??'',['draft','void'],true));
  })));
}
try{
  $method=$_SERVER['REQUEST_METHOD']??'GET';
  $action=(string)($_GET['action']??'');
  if($method==='GET'&&$action==='status'){
    result(['authenticated'=>isset($_SESSION['customer_id']),'username'=>$_SESSION['username']??null,
      'csrf'=>$_SESSION['csrf']??null]);
  }
  if($method==='POST'&&$action==='login'){
    $b=portalBody();$username=trim((string)($b['username']??''));$password=(string)($b['password']??'');
    if(strlen($username)>64||strlen($password)>2048)result(['error'=>'Invalid credentials'],401);
    if(portalThrottle($username,'check'))result(['error'=>'Too many attempts. Try again later.'],429);
    $users=portalRows('portal_auth',fn($r)=>$r);$match=null;
    foreach($users as $u){
      if(strcasecmp((string)($u['username']??''),$username)===0){$match=$u;break;}
    }
    if(!$match||empty($match['enabled'])||!password_verify($password,(string)($match['hash']??''))){
      portalThrottle($username,'failure');usleep(350000);result(['error'=>'Invalid credentials'],401);
    }
    $customers=portalRows('customers',fn($r)=>$r);$exists=false;
    foreach($customers as $c)if(($c['id']??'')===$match['customer_id']){$exists=true;break;}
    if(!$exists)result(['error'=>'Account unavailable'],403);
    portalThrottle($username,'success');
    session_regenerate_id(true);
    $_SESSION=['customer_id'=>$match['customer_id'],'username'=>$match['username'],
      'password_version'=>(int)($match['password_version']??1),'csrf'=>bin2hex(random_bytes(32))];
    portalAudit('login',$match['customer_id']);
    result(['ok'=>true,'csrf'=>$_SESSION['csrf']]);
  }
  if(!isset($_SESSION['customer_id']))result(['error'=>'Customer login required'],401);
  $customerId=(string)$_SESSION['customer_id'];
  // Every call revalidates portal enablement and password version; admin can revoke sessions.
  $auth=portalRows('portal_auth',fn($r)=>$r);
  $record=null;
  foreach($auth as $u)if(($u['customer_id']??'')===$customerId){$record=$u;break;}
  if(!$record||empty($record['enabled'])||(int)($record['password_version']??1)!==(int)($_SESSION['password_version']??1))
    result(['error'=>'Portal access revoked. Sign in again.'],401);
  if($action==='logout'&&$method==='POST'){
    portalCsrf();portalAudit('logout',$customerId);$_SESSION=[];session_destroy();result(['ok'=>true]);
  }
  if($action==='data'&&$method==='GET'){
    $customers=portalRows('customers',fn($r)=>$r);$mine=null;
    foreach($customers as $customer)if(($customer['id']??'')===$customerId){$mine=$customer;break;}
    if(!$mine)result(['error'=>'Customer record unavailable'],404);
    $safe=array_intersect_key($mine,array_flip(['id','account','name','surname','address','phone','email','package','account_status','service_type','created_at']));
    $tickets=safeRows('tickets',$customerId,['id','number','name','type','status','priority','description','created_at','updated_at']);
    $ticketIds=array_flip(array_column($tickets,'id'));
    $messages=portalRows('ticket_messages',fn($r)=>$r);
    $public=array_values(array_map(fn($r)=>array_intersect_key($r,array_flip(['id','ticket_id','body','created_at','author','source'])),
      array_filter($messages,fn($r)=>isset($ticketIds[$r['ticket_id']??''])&&($r['visibility']??'')==='public')));
    $invoices=safeRows('invoices',$customerId,['id','number','name','status','amount','invoice_date','due_date','description'],true);
    $payments=safeRows('payments',$customerId,['id','reference','invoice_id','amount','payment_date','method']);
    $services=safeRows('services',$customerId,['id','name','service_type','status','package','activated_date']);
    $usage=safeRows('usage_records',$customerId,['id','service_id','period','download_gb','upload_gb','cap_gb']);
    // NEVER disclose device management IP addresses, usernames, stored notes, or secrets.
    $equipment=safeRows('equipment',$customerId,['id','name','type','category','ownership','serial_number','warranty_expiry']);

    // Broadcast news and knowledge articles are explicitly published by staff.
    // Unpublished drafts and announcements for other branches never reach clients.
    $branch=(string)($mine['branch_id']??'');
    $news=portalRows('announcements',fn($r)=>array_values(array_map(
      fn($x)=>array_intersect_key($x,array_flip(['id','name','body','category','created_at'])),
      array_filter($r,fn($x)=>($x['published']??'')==='yes'&&
        (($x['branch_id']??'')===''||($x['branch_id']??'')===$branch)))));
    $articles=portalRows('knowledgebase',fn($r)=>array_values(array_map(
      fn($x)=>array_intersect_key($x,array_flip(['id','name','body','category','created_at'])),
      array_filter($r,fn($x)=>($x['published']??'')==='yes'))));
    result(['customer'=>$safe,'services'=>$services,'tickets'=>$tickets,'messages'=>$public,
      'invoices'=>$invoices,'payments'=>$payments,'usage'=>$usage,'equipment'=>$equipment,'announcements'=>$news,'articles'=>$articles]);
  }
  if($action==='ticket'&&$method==='POST'){
    portalCsrf();$b=portalBody();$name=trim((string)($b['name']??''));$description=trim((string)($b['description']??''));
    if(strlen($name)<4||strlen($name)>160||strlen($description)<5||strlen($description)>3000)
      result(['error'=>'Subject 4–160 characters and description 5–3000 characters required'],422);
    $customer=portalRows('customers',fn($r)=>array_values(array_filter($r,fn($x)=>($x['id']??'')===$customerId)));
    if(count($customer)!==1)result(['error'=>'Customer record missing'],404);
    $id=bin2hex(random_bytes(16));$now=gmdate('c');
    $item=['id'=>$id,'number'=>'PORTAL-'.strtoupper(substr($id,0,10)),'name'=>$name,
      'type'=>'customer','status'=>'open','priority'=>'normal','customer_id'=>$customerId,
      'branch_id'=>$customer[0]['branch_id']??'','highsite_id'=>$customer[0]['highsite_id']??'',
      'sector_id'=>$customer[0]['sector_id']??'','source'=>'portal','description'=>$description,
      'created_at'=>$now,'updated_at'=>$now,'revision'=>1];
    portalRows('tickets',function($rows,$path)use($item){$rows[]=$item;portalSave($path,$rows);});
    portalAudit('ticket_create',$customerId);
    result(['id'=>$id,'number'=>$item['number'],'name'=>$name,'status'=>'open'],201);
  }
  if($action==='reply'&&$method==='POST'){
    portalCsrf();$b=portalBody();$ticketId=(string)($b['ticket_id']??'');$body=trim((string)($b['body']??''));
    if(!preg_match('/^[a-f0-9]{32}$/',$ticketId)||strlen($body)<1||strlen($body)>4000)
      result(['error'=>'Valid ticket and message of 1–4000 characters required'],422);
    $tickets=portalRows('tickets',fn($r)=>$r);$ticket=null;
    foreach($tickets as $t)if(($t['id']??'')===$ticketId&&($t['customer_id']??'')===$customerId){$ticket=$t;break;}
    if(!$ticket)result(['error'=>'Ticket not found'],404);
    if(in_array($ticket['status']??'',['resolved','closed'],true))
      result(['error'=>'This ticket is closed. Create a new support ticket if necessary.'],409);
    $id=bin2hex(random_bytes(16));$now=gmdate('c');
    $item=['id'=>$id,'name'=>substr($body,0,100),'ticket_id'=>$ticketId,
      'customer_id'=>$customerId,'body'=>$body,'visibility'=>'public',
      'author'=>$_SESSION['username'],'source'=>'portal','created_at'=>$now,'updated_at'=>$now,'revision'=>1];
    portalRows('ticket_messages',function($rows,$path)use($item){$rows[]=$item;portalSave($path,$rows);});
    portalAudit('ticket_reply',$customerId);
    result(['id'=>$id,'ticket_id'=>$ticketId,'body'=>$body,'created_at'=>$now],201);
  }
  if($action==='password'&&$method==='POST'){
    portalCsrf();$b=portalBody();$old=(string)($b['current_password']??'');$next=(string)($b['new_password']??'');
    if(strlen($next)<14||strlen($next)>1024)result(['error'=>'New password must be 14–1024 characters'],422);
    $version=portalRows('portal_auth',function($rows,$path)use($customerId,$old,$next){
      foreach($rows as &$u)if(($u['customer_id']??'')===$customerId){
        if(!password_verify($old,(string)($u['hash']??'')))result(['error'=>'Current password incorrect'],403);
        if(hash_equals($old,$next))result(['error'=>'Choose a different password'],422);
        $u['hash']=password_hash($next,PASSWORD_DEFAULT);
        $u['password_version']=(int)($u['password_version']??1)+1;
        $u['updated_at']=gmdate('c');$v=$u['password_version'];portalSave($path,$rows);return $v;
      }
      result(['error'=>'Portal account missing'],404);
    });
    $_SESSION['password_version']=$version;session_regenerate_id(true);
    portalAudit('password_change',$customerId);result(['ok'=>true]);
  }
  result(['error'=>'Unknown action'],404);
}catch(Throwable $e){
  error_log('NetForge customer portal: '.$e->getMessage());
  result(['error'=>'Portal server error'],500);
}
