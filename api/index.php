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
// Per-IP-and-username throttling. Remote address is server-provided, never a forwarded client header.
// This is deliberately file-backed so it runs on shared cPanel without SQL or Redis.
function loginThrottle(string $username,string $mode): bool {
    $key=hash('sha256', (string)($_SERVER['REMOTE_ADDR']??'unknown').'|'.strtolower($username));
    return locked('login_attempts',function($rows,$path)use($key,$mode){
        $now=time();
        $rows=array_values(array_filter($rows,fn($x)=>is_array($x)&&isset($x['time'])&&((int)$x['time'])>($now-900)));
        $count=count(array_filter($rows,fn($x)=>($x['key']??'')===$key));
        if($mode==='failure'){$rows[]=['key'=>$key,'time'=>$now];if(count($rows)>20000)$rows=array_slice($rows,-20000);savejson($path,$rows);}
        elseif($mode==='success'){$rows=array_values(array_filter($rows,fn($x)=>($x['key']??'')!==$key));savejson($path,$rows);}
        return $count>=8;
    });
}
function audit(string $action, string $entity, string $id): void { locked('audit',function($rows,$path)use($action,$entity,$id){ $rows[]=['time'=>gmdate('c'),'user'=>$_SESSION['username']??'unknown','action'=>$action,'entity'=>$entity,'id'=>$id]; if(count($rows)>10000) $rows=array_slice($rows,-10000); savejson($path,$rows); }); }
// Annualized-fee assumptions are intentionally excluded. One invoice is generated for
// each active service with an explicit monthly fee or a matching priced package.
// Prices are handled as integer cents and the result is stable across retries.
function amountCents(string $value): ?int {
    if(!preg_match('/^(?:0|[1-9][0-9]{0,8})(?:\.[0-9]{1,2})?$/',$value))return null;
    $parts=explode('.',$value,2);
    return (int)$parts[0]*100+(int)str_pad($parts[1]??'',2,'0');
}
function billingProposal(string $period,array $invoices): array {
    if(!preg_match('/^20[0-9]{2}-(0[1-9]|1[0-2])$/',$period))respond(['error'=>'Use a billing period YYYY-MM between 2000 and 2099'],422);
    $customers=locked('customers',fn($r)=>$r);
    $services=locked('services',fn($r)=>$r);
    $packages=locked('packages',fn($r)=>$r);
    $byCustomer=[];foreach($customers as $c)$byCustomer[$c['id']]=$c;
    $byPackage=[];foreach($packages as $p)if(isset($p['name']))$byPackage[mb_strtolower(trim((string)$p['name']))]=$p;
    $existing=[];
    foreach($invoices as $invoice)if(($invoice['billing_period']??'')===$period&&($invoice['source_service_id']??'')!=='')$existing[$invoice['source_service_id']]=true;
    $ready=[];$skipped=[];$already=[];
    foreach($services as $service){
        if(($service['status']??'')!=='active')continue;
        $id=(string)($service['id']??'');$customer=$byCustomer[$service['customer_id']??'']??null;
        $customerName=$customer['account']??'';
        if(!$customer||in_array($customer['account_status']??'',['cancelled','suspended'],true)){
            $skipped[]=['service_id'=>$id,'service'=>$service['name']??'','reason'=>'Missing or inactive customer'];continue;
        }
        $packageName=trim((string)($service['package']??''));
        $package=$byPackage[mb_strtolower($packageName)]??null;
        $fee=trim((string)($service['monthly_fee']??''));
        if($fee==='')$fee=trim((string)($package['price']??''));
        $cents=amountCents($fee);
        if($cents===null||$cents===0){
            $skipped[]=['service_id'=>$id,'service'=>$service['name']??'','customer'=>$customerName,'reason'=>'Missing or invalid monthly service/package price'];continue;
        }
        $billDay=(int)($customer['billing_day']??0);
        if($billDay<1||$billDay>28)$billDay=1;
        $row=[
            'service_id'=>$id,'service'=>$service['name']??'',
            'customer_id'=>$customer['id'],'customer'=>$customerName,
            'branch_id'=>$customer['branch_id']??'',
            'package'=>$packageName,'amount'=>number_format($cents/100,2,'.',''),
            'due_date'=>sprintf('%s-%02d',$period,$billDay)
        ];
        if(isset($existing[$id]))$already[]=$row;else $ready[]=$row;
    }
    usort($ready,fn($a,$b)=>strcmp($a['service_id'],$b['service_id']));
    usort($already,fn($a,$b)=>strcmp($a['service_id'],$b['service_id']));
    $sum=0;foreach($ready as $row)$sum+=(int)round((float)$row['amount']*100);
    $token=hash('sha256',json_encode([$period,$ready,$already,$skipped],JSON_THROW_ON_ERROR));
    return ['period'=>$period,'ready'=>$ready,'existing'=>$already,'skipped'=>$skipped,'ready_total'=>number_format($sum/100,2,'.',''),'token'=>$token,
        'warning'=>'Draft invoices are not sent automatically. No VAT, pro-rating, payment gateway or invoice PDF is calculated.'];
}

try {
    $method=$_SERVER['REQUEST_METHOD']??'GET';
    $action=$_GET['action']??'';
    if ($method==='GET' && $action==='status') {
        $installed=is_file(datafile('auth'));
        respond(['installed'=>$installed,'authenticated'=>!empty($_SESSION['user']),'username'=>$_SESSION['username']??null,'role'=>$_SESSION['role']??null,'csrf'=>$_SESSION['csrf']??null]);
    }
    if ($method==='POST' && $action==='setup') {
        if (is_file(datafile('auth'))) respond(['error'=>'Already configured'],409);
        $b=body(); $user=trim((string)($b['username']??'')); $pass=(string)($b['password']??'');
        if (!preg_match('/^[a-zA-Z0-9_.-]{3,40}$/',$user) || strlen($pass)<14) respond(['error'=>'Username must be 3-40 valid characters and password at least 14 characters'],422);
        $handle=@fopen(datafile('auth'),'x');
        if (!$handle) respond(['error'=>'Already configured or cannot initialize'],409);
        $auth=['username'=>$user,'hash'=>password_hash($pass,PASSWORD_DEFAULT)];
        fwrite($handle,json_encode($auth,JSON_THROW_ON_ERROR)); fclose($handle); chmod(datafile('auth'),0600);
        session_regenerate_id(true); $_SESSION['user']=true; $_SESSION['username']=$user; $_SESSION['role']='admin'; $_SESSION['csrf']=bin2hex(random_bytes(32));
        respond(['ok'=>true,'csrf'=>$_SESSION['csrf']],201);
    }
    if ($method==='POST' && $action==='login') {
        $b=body(); $attemptUser=(string)($b['username']??'');
        if(loginThrottle($attemptUser,'check')) respond(['error'=>'Too many login attempts. Try again later.'],429);
        $auth=json_decode((string)@file_get_contents(datafile('auth')),true);
        $matched=is_array($auth)&&hash_equals($auth['username'],(string)($b['username']??''))&&password_verify((string)($b['password']??''),$auth['hash']);
        if(!$matched){$staff=locked('staff',fn($r)=>$r);foreach($staff as $member){if(hash_equals((string)($member['username']??''),(string)($b['username']??''))&&password_verify((string)($b['password']??''),(string)($member['password_hash']??''))){$matched=true;$auth=['username'=>$member['username'],'role'=>$member['role']??'viewer'];break;}}}
        if(!$matched){loginThrottle($attemptUser,'failure');usleep(350000);respond(['error'=>'Invalid credentials'],401);}loginThrottle($attemptUser,'success');
        session_regenerate_id(true); $_SESSION['user']=true; $_SESSION['username']=$auth['username']; $_SESSION['role']=$auth['role']??'admin'; $_SESSION['csrf']=bin2hex(random_bytes(32));
        respond(['ok'=>true,'csrf'=>$_SESSION['csrf']]);
    }
    if (empty($_SESSION['user'])) respond(['error'=>'Authentication required'],401);
    if ($method==='POST' && $action==='logout') { csrf(); $_SESSION=[]; session_destroy(); respond(['ok'=>true]); }

    // Draft monthly billing runs are admin-only by design: they create financial records.
    if($action==='billing_preview'&&$method==='GET'){
        if(!in_array($_SESSION['role']??'',['admin','dispatcher'],true))respond(['error'=>'Finance access requires admin or dispatcher'],403);
        $period=(string)($_GET['period']??'');
        respond(locked('invoices',fn($rows)=>billingProposal($period,$rows)));
    }
    if($action==='billing_generate'&&$method==='POST'){
        if(($_SESSION['role']??'')!=='admin')respond(['error'=>'Only an administrator can generate invoice drafts'],403);
        csrf();$b=body();$period=(string)($b['period']??'');$expected=(string)($b['token']??'');
        if(!preg_match('/^[a-f0-9]{64}$/',$expected))respond(['error'=>'Valid preview token required'],422);
        $result=locked('invoices',function($rows,$path)use($period,$expected){
            $proposal=billingProposal($period,$rows);
            if(!hash_equals($proposal['token'],$expected))respond(['error'=>'Billing data changed. Refresh preview and review again.'],409);
            if(count($proposal['ready'])>500)respond(['error'=>'Limit: 500 draft invoices per billing run'],413);
            $created=[];$now=gmdate('c');
            foreach($proposal['ready'] as $entry){
                $id=bin2hex(random_bytes(16));$created[]=[
                    'id'=>$id,'name'=>'Recurring service: '.$entry['service'],'number'=>'NF-'.str_replace('-','',$period).'-'.substr($entry['service_id'],0,16),
                    'customer_id'=>$entry['customer_id'],'branch_id'=>$entry['branch_id'],
                    'invoice_date'=>$period.'-01','due_date'=>$entry['due_date'],
                    'amount'=>$entry['amount'],'status'=>'draft',
                    'description'=>'Monthly service '.$entry['service'].($entry['package']!==''?' ('.$entry['package'].')':''),
                    'billing_period'=>$period,'source_service_id'=>$entry['service_id'],
                    'created_at'=>$now,'updated_at'=>$now,'revision'=>1
                ];
            }
            if($created)savejson($path,array_merge($rows,$created));
            return ['created'=>count($created),'existing'=>count($proposal['existing']),'skipped'=>count($proposal['skipped']),'total'=>$proposal['ready_total'],'period'=>$period,'ids'=>array_column($created,'id')];
        });
        audit('billing_generate','invoices',$period);
        respond($result,201);
    }
    $allowed=['customers','highsites','sectors','equipment','backhauls','scripts','jobs','tickets','branches','technicians','packages','staff','outages','maintenance','inventory','leads','changes','ipam','activities','services','invoices','payments','surveys','workorders','link_checks'];
    $category=(string)($_GET['category']??'');
    if (!in_array($category,$allowed,true)) respond(['error'=>'Unknown category'],404);
    if ($category==='staff' && ($_SESSION['role']??'')!=='admin') respond(['error'=>'Admin only'],403);
    if ($method==='GET') respond(locked($category,function($rows)use($category){if($category==='staff')return array_map(fn($x)=>['id'=>$x['id'],'name'=>$x['name']??'','username'=>$x['username']??'','role'=>$x['role']??'viewer','created_at'=>$x['created_at']??''],$rows);return $rows;}));
    csrf();
    if (($_SESSION['role']??'viewer')==='viewer') respond(['error'=>'Read-only role'],403);
    if ($method==='POST' || $method==='PUT') {
        $b=body();
        if ($category==='jobs') respond(['error'=>'Script execution requires an external agent'],403);
        $name=trim((string)($b['name']??$b['title']??$b['account']??''));
        if ($name==='' || strlen($name)>160) respond(['error'=>'Name/account/title required (max 160 bytes)'],422);
        $fields=[
          'customers'=>['account','name','surname','address','phone','email','package','branch_id','highsite_id','sector_id','latitude','longitude','cpe_ip','cpe_username','notes','account_status','service_type','installation_date','contract_end','billing_day','monthly_fee','vlan','pppoe_username','static_ip','cpe_mac','signal_dbm','last_contact','lead_source','emergency_contact'],
          'tickets'=>['name','number','type','status','priority','customer_id','highsite_id','branch_id','technician_id','scheduled_date','description','call_log','outage_id','source','impact','fault_category','opened_date','due_date','resolved_date','sla_hours','resolution','repeat_fault','contact_name','contact_phone'],
          'highsites'=>['name','number','address','branch_id','latitude','longitude','notes','site_status','tower_height','site_owner','lease_expiry','site_contact','access_instructions','site_code','power_source','battery_capacity','ups_runtime','generator','last_inspection','next_inspection'],
          'equipment'=>['name','type','category','highsite_id','customer_id','ownership','serial_number','ip','username','protocol','port','notes','mac','firmware','purchase_date','warranty_expiry','supplier','purchase_price','asset_tag','installation_date','last_service','next_service','condition','poe_type','vlan'],
          'branches'=>['name','address','phone','manager','email','region'],
          'technicians'=>['name','surname','branch_id','phone','active','skills','employee_number','email','on_call'],
          'scripts'=>['name','language','description','body','risk','vendor','version','approved','target_scope'],
          'staff'=>['name','username','role'],
          'sectors'=>['name','highsite_id','azimuth','frequency','type','ip','username','protocol','port','notes','ssid','channel_width','polarization','antenna_gain','tx_power','max_clients','sector_status','radio_mac','tower_height','beamwidth'],
          'backhauls'=>['name','highsite_id','remote_highsite_id','frequency','notes','remote_ip','link_distance','target_rssi','channel_width','link_capacity','link_status'],
          'packages'=>['name','speed','price','upload_speed','download_speed','contention','data_policy'],
          'outages'=>['name','number','status','severity','branch_id','highsite_id','start_date','end_date','affected_customers','root_cause','resolution'],
          'maintenance'=>['name','highsite_id','equipment_id','branch_id','technician_id','due_date','completed_date','status','checklist'],
          'inventory'=>['name','sku','category','branch_id','quantity','minimum','unit_cost','supplier','location','serial_tracking'],
          'leads'=>['name','phone','email','address','branch_id','stage','requested_package','follow_up','notes'],
          'changes'=>['name','change_type','risk','status','highsite_id','technician_id','planned_date','rollback_plan','description'],
          'ipam'=>['name','address','vlan','branch_id','highsite_id','customer_id','purpose','status','notes'],
          'activities'=>['name','customer_id','ticket_id','type','channel','description','occurred_at','performed_by'],
          'services'=>['name','customer_id','service_type','status','package','monthly_fee','highsite_id','sector_id','vlan','ip','pppoe_username','activated_date','notes'],
          'invoices'=>['name','number','customer_id','branch_id','invoice_date','due_date','amount','status','description'],
          'payments'=>['name','reference','customer_id','invoice_id','payment_date','amount','method','notes'],
          'surveys'=>['name','customer_id','lead_id','branch_id','highsite_id','latitude','longitude','azimuth','distance_km','fresnel_clearance','los_status','status','notes'],
          'workorders'=>['name','ticket_id','customer_id','highsite_id','technician_id','branch_id','scheduled_date','status','checklist','materials','completion_notes'],
          'link_checks'=>['name','customer_id','sector_id','rssi_dbm','snr_db','ccq','tx_mbps','rx_mbps','captured_at','notes'],
        ];
        $item=[];
        foreach(($fields[$category]??['name','description']) as $field){
          $value=$b[$field]??'';
          if(!is_scalar($value)) respond(['error'=>'Invalid field '.$field],422);
          $item[$field]=substr(trim((string)$value),0,in_array($field,['description','notes','call_log','resolution','root_cause','rollback_plan','access_instructions','checklist'],true)?4000:($field==='body'?16000:300));
        }
        if($category==='tickets'&&!in_array($item['type'],['customer','highsite','voip','other'],true))respond(['error'=>'Invalid ticket type'],422);
        if($category==='equipment'&&!in_array($item['category'],['distribution','backhaul','access','power','cpe','router','other'],true))respond(['error'=>'Invalid equipment category'],422);
        if($category==='customers'){
          if(trim((string)($item['account']??''))==='')respond(['error'=>'An account identifier is required'],422);
          $hs=$item['highsite_id']??'';$sector=$item['sector_id']??'';
          if($sector!==''&&$hs==='')respond(['error'=>'Select a highsite before choosing a sector'],422);
          if($hs!==''){$sites=locked('highsites',fn($r)=>$r);if(!array_filter($sites,fn($r)=>($r['id']??'')===$hs))respond(['error'=>'Highsite does not exist'],422);}
          if($sector!==''){$sectors=locked('sectors',fn($r)=>$r);if(!array_filter($sectors,fn($r)=>($r['id']??'')===$sector&&($r['highsite_id']??'')===$hs))respond(['error'=>'Sector does not belong to selected highsite'],422);}
        }
        if($category==='sectors'&&($item['highsite_id']??'')==='')respond(['error'=>'Sector requires a highsite'],422);
        if($category==='equipment'){
          $hs=$item['highsite_id']??'';$customer=$item['customer_id']??'';
          if($hs===''&&$customer==='')respond(['error'=>'Assign equipment to a highsite or customer'],422);
          if($hs!==''&&$customer!=='')respond(['error'=>'Equipment cannot belong to a highsite and customer simultaneously'],422);
          if($customer!==''){$customers=locked('customers',fn($r)=>$r);if(!array_filter($customers,fn($r)=>($r['id']??'')===$customer))respond(['error'=>'Customer does not exist'],422);}
          if($hs!==''){$sites=locked('highsites',fn($r)=>$r);if(!array_filter($sites,fn($r)=>($r['id']??'')===$hs))respond(['error'=>'Highsite does not exist'],422);}
        }

        // Non-executable operational records must use valid finite values; never infer telemetry.
        if(in_array($category,['payments','invoices'],true)){
          if(!is_numeric($item['amount']??null) || (float)$item['amount']<0 || !is_finite((float)$item['amount']))
            respond(['error'=>'Amount must be a finite nonnegative number'],422);
          $item['amount']=number_format((float)$item['amount'],2,'.','');
        }
        if($category==='activities') {
          $item['performed_by']=$_SESSION['username'];
          if(!in_array($item['type'],['call','note','email','site-visit','whatsapp'],true))
             respond(['error'=>'Invalid activity type'],422);
          if($item['occurred_at']==='')$item['occurred_at']=gmdate('c');
        }
        if($category==='services'){
          if($item['customer_id']==='') respond(['error'=>'Service requires a customer'],422);
          if($item['sector_id']!==''&&$item['highsite_id']==='')respond(['error'=>'Select highsite before sector'],422);
          if($item['sector_id']!==''){
             $sectors=locked('sectors',fn($r)=>$r);
             if(!array_filter($sectors,fn($r)=>($r['id']??'')===$item['sector_id']&&($r['highsite_id']??'')===$item['highsite_id']))
                respond(['error'=>'Serving sector does not belong to selected highsite'],422);
          }
        }
        if($category==='link_checks'){
          if($item['customer_id']===''&&$item['sector_id']==='')respond(['error'=>'Choose a customer or a sector'],422);
          if($item['captured_at']==='')$item['captured_at']=gmdate('c');
        }
        if($category==='ipam'){
          $addr=trim($item['address']??'');
          if($addr==='')respond(['error'=>'IP or CIDR is required'],422);
          $existing=locked('ipam',fn($r)=>$r);
          $duplicate=array_filter($existing,fn($r)=>strcasecmp((string)($r['address']??''),$addr)===0&&($r['id']??'')!==($b['id']??''));
          if($duplicate)respond(['error'=>'Duplicate IP/CIDR record. Review the existing allocation first.'],409);
        }


        if($category==='payments'&&($item['invoice_id']??'')!==''){
          $matching=locked('invoices',fn($r)=>array_values(array_filter($r,fn($i)=>($i['id']??'')===$item['invoice_id'])));
          if(count($matching)!==1)respond(['error'=>'Referenced invoice does not exist'],422);
          if(($matching[0]['customer_id']??'')!==($item['customer_id']??''))
            respond(['error'=>'Payment customer must match the referenced invoice customer'],422);
        }
        if($category==='activities'&&($item['customer_id']??'')==='')respond(['error'=>'Communication event requires a customer'],422);
        if($category==='inventory'){
          foreach(['quantity','minimum'] as $countField){
            if(($item[$countField]??'')!==''&&(!ctype_digit((string)$item[$countField])||strlen($item[$countField])>9))
              respond(['error'=>"$countField must be a nonnegative whole number"],422);
          }
          if(($item['unit_cost']??'')!==''&&(!is_numeric($item['unit_cost'])||(float)$item['unit_cost']<0))
            respond(['error'=>'Unit cost must be nonnegative'],422);
        }
        if($category==='ipam'){
          $addr=$item['address'];$parts=explode('/',$addr);
          if(count($parts)>2||filter_var($parts[0],FILTER_VALIDATE_IP)===false)
            respond(['error'=>'Enter a valid IPv4/IPv6 address or CIDR'],422);
          if(count($parts)===2){
            $max=filter_var($parts[0],FILTER_VALIDATE_IP,FILTER_FLAG_IPV4)?32:128;
            if($parts[1]===''||!ctype_digit($parts[1])||(int)$parts[1]>$max)
              respond(['error'=>'Invalid IP prefix length'],422);
          }
        }
        if($category==='sectors'&&($item['azimuth']??'')!==''){
          $az=$item['azimuth'];
          if(!is_numeric($az)||!is_finite((float)$az)||(float)$az<0||(float)$az>=360)
            respond(['error'=>'Azimuth must be in the range 0 to less than 360 degrees'],422);
        }
        if($category==='staff'){
          if(($_SESSION['role']??'')!=='admin')respond(['error'=>'Admin only'],403);
          if($method==='PUT')respond(['error'=>'Staff updates not yet supported'],405);
          if(!in_array($item['role'],['admin','dispatcher','technician','viewer'],true))respond(['error'=>'Invalid role'],422);
          $password=(string)($b['password']??'');
          if(strlen($password)<14)respond(['error'=>'Staff password must be at least 14 characters'],422);
          $item['password_hash']=password_hash($password,PASSWORD_DEFAULT);
        }
        if($method==='PUT'){
          $id=(string)($b['id']??'');if(!preg_match('/^[a-f0-9]{32}$/',$id))respond(['error'=>'Invalid ID'],422);
          $expectedRevision=(int)($b['revision']??1);
          if($expectedRevision<1)respond(['error'=>'Invalid revision'],422);
          $item['id']=$id;$item['updated_at']=gmdate('c');
          $saved=locked($category,function($rows,$path)use($item,$id,$expectedRevision,$category){$found=false;foreach($rows as &$row){if(($row['id']??'')===$id){
              if((int)($row['revision']??1)!==$expectedRevision)respond(['error'=>'This record has changed since it was opened. Refresh and try again.'],409);
              if($category==='invoices'&&($row['source_service_id']??'')!=='')
                respond(['error'=>'Generated billing records cannot be manually overwritten.'],409);
              if($category==='sectors'&&($row['highsite_id']??'')!==($item['highsite_id']??'')){
                 $assigned=locked('customers',fn($r)=>count(array_filter($r,fn($x)=>($x['sector_id']??'')===$id)));
                 if($assigned)respond(['error'=>'Cannot move sector to another highsite while subscribers are assigned'],409);
              }
              $item['revision']=$expectedRevision+1;
              $item['created_at']=$row['created_at']??gmdate('c');$row=$item;$found=true;break;}}unset($row);if(!$found)respond(['error'=>'Not found'],404);savejson($path,$rows);return $item;});
          audit('update',$category,$id);respond($saved);
        }
        $item['id']=bin2hex(random_bytes(16));$item['created_at']=gmdate('c');$item['updated_at']=$item['created_at'];$item['revision']=1;
        locked($category,function($rows,$path)use($item,$category){if($category==='staff'){foreach($rows as $r){if(strcasecmp($r['username']??'',$item['username'])===0)respond(['error'=>'Username exists'],409);}}if($category==='customers'&&($item['account']??'')!==''){
         foreach($rows as $existing){if(strcasecmp((string)($existing['account']??''),$item['account'])===0)respond(['error'=>'Customer account name already exists'],409);}
       }
       $rows[]=$item;savejson($path,$rows);});
        audit('create',$category,$item['id']);
        if($category==='staff')unset($item['password_hash']);
        respond($item,201);
    }
    if ($method==='DELETE') {
        if (($_SESSION['role']??'')!=='admin') respond(['error'=>'Admin only'],403);
        $id=(string)($_GET['id']??'');if (!preg_match('/^[a-f0-9]{32}$/',$id)) respond(['error'=>'Invalid ID'],422);
        if ($category==='jobs') respond(['error'=>'Job execution is disabled'],403);
        // Prevent destructive deletions that would orphan related records.
        // Destructive operation guards across all typed relationships.
        $references=[
          'highsites'=>[['sectors','highsite_id'],['customers','highsite_id'],['equipment','highsite_id'],['backhauls','highsite_id'],['backhauls','remote_highsite_id'],['tickets','highsite_id'],['services','highsite_id'],['surveys','highsite_id'],['workorders','highsite_id'],['outages','highsite_id'],['maintenance','highsite_id'],['changes','highsite_id'],['ipam','highsite_id']],
          'sectors'=>[['customers','sector_id'],['services','sector_id'],['link_checks','sector_id']],
          'customers'=>[['tickets','customer_id'],['equipment','customer_id'],['activities','customer_id'],['services','customer_id'],['invoices','customer_id'],['payments','customer_id'],['surveys','customer_id'],['workorders','customer_id'],['link_checks','customer_id'],['ipam','customer_id']],
          'branches'=>[['customers','branch_id'],['highsites','branch_id'],['technicians','branch_id'],['tickets','branch_id'],['surveys','branch_id'],['invoices','branch_id'],['workorders','branch_id'],['inventory','branch_id'],['leads','branch_id'],['ipam','branch_id'],['outages','branch_id'],['changes','branch_id'],['maintenance','branch_id']],
          'technicians'=>[['tickets','technician_id'],['workorders','technician_id'],['maintenance','technician_id'],['changes','technician_id']],
          'equipment'=>[['maintenance','equipment_id']],
          'invoices'=>[['payments','invoice_id']],
          'tickets'=>[['activities','ticket_id'],['workorders','ticket_id']],
          'leads'=>[['surveys','lead_id']],
        ];
        foreach(($references[$category]??[]) as [$collection,$field]){
          $used=locked($collection,fn($rows)=>count(array_filter($rows,fn($row)=>($row[$field]??'')===$id)));
          if($used>0)respond(['error'=>"Cannot delete: $used related record(s) in $collection. Reassign them first."],409);
        }
        if($category==='invoices'){
           $generated=locked('invoices',fn($rows)=>count(array_filter($rows,fn($r)=>($r['id']??'')===$id&&($r['source_service_id']??'')!=='')));
           if($generated)respond(['error'=>'Generated invoice drafts cannot be deleted; retain them for audit'],409);
        }
        $deleted=locked($category,function($rows,$path)use($id){$next=array_values(array_filter($rows,fn($r)=>($r['id']??'')!==$id));if(count($next)===count($rows))return false;savejson($path,$next);return true;});
        if (!$deleted) respond(['error'=>'Not found'],404);
        audit('delete',$category,$id);respond(['ok'=>true]);
    }
    respond(['error'=>'Method not allowed'],405);
} catch (Throwable $e) { error_log('NetForge API: '.$e->getMessage()); respond(['error'=>'Internal server error'],500); }
