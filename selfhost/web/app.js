// root/selfhost/web/app.js
'use strict';
const root=document.querySelector('#root'),identity=document.querySelector('#identity'),logout=document.querySelector('#logout');
let csrf='',role='',username='',tab='Dashboard',data={};
const views=['Dashboard','Customers','Services','Packages','Highsites','Sectors','Equipment','RADIUS NAS','RADIUS Sessions','Tickets','Billing','Staff'];
const esc=v=>String(v??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;','\'':'&#39;'}[c]));
const money=n=>new Intl.NumberFormat('en-ZA',{style:'currency',currency:'ZAR'}).format(Number(n)||0);
async function api(path,method='GET',payload){
 const opt={method,credentials:'same-origin',headers:{Accept:'application/json'}};
 if(method!=='GET'&&csrf)opt.headers['X-CSRF-Token']=csrf;
 if(payload!==undefined){opt.headers['Content-Type']='application/json';opt.body=JSON.stringify(payload)}
 const response=await fetch('/api'+path,opt);
 let body;try{body=await response.json()}catch{throw Error('Server returned an invalid response')}
 if(!response.ok)throw Error(body.error||body.title||'Request failed ('+response.status+')');
 return body;
}
function login(){
 root.innerHTML='<main><div class="login card"><h1>Sign in to NetForge</h1><p class="sub">Administrator, dispatcher or technician access.</p><form id="signin"><label>Username</label><input name="username" required autocomplete="username"><label>Password</label><input name="password" type="password" required autocomplete="current-password"><p id="loginerror" class="error" role="alert"></p><button class="primary">Sign in</button></form></div></main>';
 logout.hidden=true;identity.textContent='';
 root.querySelector('#signin').onsubmit=async e=>{e.preventDefault();try{const response=await api('/login','POST',Object.fromEntries(new FormData(e.target)));csrf=response.csrf;role=response.role;username=response.username;await boot(true)}catch(err){root.querySelector('#loginerror').textContent=err.message}};
}
async function boot(skipAuth=false){
 try{
  if(!skipAuth){const session=await api('/status');csrf=session.csrf;role=session.role;username=session.username}
  identity.textContent=username+' · '+role;logout.hidden=false;
  root.innerHTML='<div class="shell"><nav id="nav"></nav><main id="content"></main></div>';
  views.forEach(v=>{if(v==='Staff'&&role!=='admin')return;if(v==='Billing'&&!['admin','dispatcher'].includes(role))return;if(v==='RADIUS NAS'&&!['admin','dispatcher'].includes(role))return;
   let b=document.createElement('button');b.textContent=v;b.onclick=()=>{tab=v;render()};b.dataset.tab=v;root.querySelector('#nav').append(b)});
  await refresh();
 }catch{login()}
}
async function refresh(){
 const names=['customers','services','packages','highsites','sectors','tickets','branches'];
 const endpoints={customers:'/customers',services:'/services',packages:'/packages',highsites:'/highsites',sectors:'/sectors',tickets:'/tickets',
  nas:'/radius/nas',sessions:'/radius/sessions',invoices:'/invoices',staff:'/staff',equipment:'/equipment'};
 const need=tab==='RADIUS NAS'?['nas']:tab==='RADIUS Sessions'?['sessions']:tab==='Billing'?['invoices']:tab==='Staff'?['staff']:tab==='Equipment'?['equipment']:[];
 const failed=[];
 await Promise.all([...names,...need].map(async n=>{try{data[n]=await api(endpoints[n])}catch(e){failed.push(n+': '+e.message);data[n]=[]}}));
 render();
 if(failed.length)notice('Unavailable datasets: '+failed.join('; '),true);
}
function render(){
 root.querySelectorAll('nav button').forEach(b=>b.className=b.dataset.tab===tab?'active':'');
 const content=root.querySelector('#content');content.innerHTML='<div class="sub">NETFORGE · '+esc(tab.toUpperCase())+'</div><p id="message" role="status"></p>';
 if(tab==='Dashboard')dashboard(content);
 else if(tab==='Customers')list(content,'customers','Customer accounts',['account','first_name','surname','phone','status','highsite','sector']);
 else if(tab==='Services')list(content,'services','Customer services & PPPoE',['account','username','package','state','static_ipv4','radius_enabled']);
 else if(tab==='Packages')list(content,'packages','Service packages',['name','download_mbps','upload_mbps','monthly_price','mikrotik_rate_limit']);
 else if(tab==='Highsites')list(content,'highsites','Highsites',['number','name','branch','latitude','longitude','subscribers']);
 else if(tab==='Sectors')list(content,'sectors','Access sectors',['highsite','name','ssid','azimuth','frequency_mhz','max_customers','customers']);
 else if(tab==='Equipment')list(content,'equipment','Site & customer equipment',['name','model','category','account','highsite','management_ip','serial_number','ownership']);
 else if(tab==='RADIUS NAS')list(content,'nas','RADIUS NAS clients',['shortname','nasname','type','description']);
 else if(tab==='RADIUS Sessions')list(content,'sessions','RADIUS accounting sessions',['username','nas_ip','assigned_ip','acctstarttime','acctupdatetime','recently_seen']);
 else if(tab==='Tickets')list(content,'tickets','Support tickets',['number','subject','account','type','priority','status','created_at']);
 else if(tab==='Billing')list(content,'invoices','Invoices',['number','account','total','paid','outstanding','status','due_date']);
 else if(tab==='Staff')list(content,'staff','Staff accounts',['username','role','enabled','created_at']);
}
function notice(text,isError=false){
 const target=root.querySelector('#message');if(target){target.className=isError?'error':'notice';target.textContent=text}else alert(text);
}
function dashboard(el){
 el.insertAdjacentHTML('beforeend','<h1>ISP Operations</h1><p class="sub">CRM, subscriber services, RADIUS and network infrastructure.</p><div id="stats" class="grid"></div><div class="card topgap"><h2>Operational context</h2><p>RADIUS authentication and accounting are supplied by FreeRADIUS. Adding a NAS requires a FreeRADIUS restart. Suspending a service stops new authentications but does not disconnect an already established session.</p><button id="refresh" class="primary">Refresh dashboard</button></div>');
 api('/dashboard').then(r=>{const stats=[['Customers',r.customers],['Highsites',r.highsites],['Sectors',r.sectors],['Active services',r.active_services],['Open tickets',r.open_tickets],['Unclosed RADIUS sessions',r.unclosed_radius_sessions],['Invoices',r.invoices],['NAS devices',r.nas_devices]];
  el.querySelector('#stats').innerHTML=stats.map(([name,count])=>'<div class="card"><small>'+esc(name)+'</small><div class="metric">'+count+'</div></div>').join('');
 }).catch(e=>notice(e.message,true));
 el.querySelector('#refresh').onclick=refresh;
}
const forms={
 customers:[['account','Account name'],['firstName','First name'],['surname','Surname'],['email','Email'],['phone','Phone'],['address','Address','textarea'],['status','Status','status'],['branchId','Branch','branches'],['highsiteId','Highsite','highsites'],['sectorId','Sector','sectors']],
 services:[['customerId','Customer','customers'],['packageId','Package','packages'],['username','PPPoE username'],['state','Initial state','serviceState'],['staticIpv4','Static IPv4 (optional)']],
 packages:[['name','Package name'],['downloadMbps','Download Mbps','number'],['uploadMbps','Upload Mbps','number'],['monthlyPrice','Monthly price (ZAR)','decimal'],['mikrotikRateLimit','MikroTik-Rate-Limit (optional)']],
 highsites:[['number','Highsite number'],['name','Site name'],['address','Address'],['branchId','Branch','branches'],['latitude','Latitude','decimal'],['longitude','Longitude','decimal']],
 equipment:[['customerId','Customer','customers'],['highsiteId','Highsite','highsites'],['category','Category','equipCategory'],['name','Internal equipment name'],['model','Device model'],['managementIp','Management IP'],['serialNumber','Serial number'],['ownership','Ownership']],
 sectors:[['highsiteId','Highsite','highsites'],['name','Sector name'],['ssid','SSID'],['azimuth','Azimuth','decimal'],['frequencyMhz','Frequency MHz','decimal'],['maxCustomers','Planned subscribers','number']],
 nas:[['address','NAS IPv4 address'],['name','NAS name'],['secret','RADIUS shared secret (16+ characters)','password'],['description','Description']],
 tickets:[['customerId','Customer','customers'],['highsiteId','Highsite','highsites'],['subject','Subject'],['description','Description','textarea'],['type','Service type','ticketType'],['priority','Priority','priority']],
 invoices:[['customerId','Customer','customers'],['number','Invoice number'],['total','Amount','decimal'],['dueDate','Due date','date']],
 staff:[['username','Username'],['password','Password (14+ characters)','password'],['role','Role','role']]
};
const choices={equipCategory:['cpe','router','other','distribution','backhaul','access','power'],status:['active','suspended','pending','cancelled'],serviceState:['pending','suspended','cancelled'],ticketType:['customer','highsite','voip','other'],priority:['normal','low','high','critical'],role:['viewer','technician','dispatcher','admin']};
function lookupTable(collection){
 const arr=data[collection]||[];
 if(collection==='customers')return arr.map(x=>({id:x.id,label:x.account+' · '+x.first_name+' '+x.surname}));
 if(collection==='highsites')return arr.map(x=>({id:x.id,label:x.number+' · '+x.name}));
 if(collection==='sectors')return arr.map(x=>({id:x.id,label:x.name,highsite_id:x.highsite_id}));
 return arr.map(x=>({id:x.id,label:x.name}));
}
function formField([key,label,type],values={}){
 const value=values[key]??'',id='i_'+key;
 if(type==='textarea')return '<div class="wide"><label for="'+id+'">'+esc(label)+'</label><textarea id="'+id+'" name="'+key+'" rows="3">'+esc(value)+'</textarea></div>';
 if(type in choices)return '<div><label for="'+id+'">'+esc(label)+'</label><select id="'+id+'" name="'+key+'">'+choices[type].map(v=>'<option value="'+esc(v)+'" '+(v===value?'selected':'')+'>'+esc(v)+'</option>').join('')+'</select></div>';
 if(['customers','highsites','sectors','packages','branches'].includes(type))return '<div><label for="'+id+'">'+esc(label)+'</label><select id="'+id+'" name="'+key+'"><option value="">Unassigned</option>'+lookupTable(type).map(x=>'<option value="'+esc(x.id)+'" '+(x.id===value?'selected':'')+'>'+esc(x.label)+'</option>').join('')+'</select></div>';
 return '<div><label for="'+id+'">'+esc(label)+'</label><input id="'+id+'" name="'+key+'" '+(type==='password'?'type="password" autocomplete="new-password" minlength="12"':type==='number'?'type="number" step="1"':type==='decimal'?'type="number" step="0.01"':type==='date'?'type="date"':'')+' value="'+esc(value)+'"></div>';
}
function dialog(html){
 const d=document.createElement('dialog');d.innerHTML=html;document.body.append(d);d.onclose=()=>d.remove();d.showModal();return d;
}
function create(collection,values={}){
 const fields=forms[collection];if(!fields)return;
 const d=dialog('<div class="toolbar"><h2>Create '+esc(collection)+'</h2><button id="close">Close</button></div><form id="entry"><div class="formgrid">'+fields.map(f=>formField(f,values)).join('')+'</div><p id="error" class="error"></p><button class="primary">Save record</button></form>');
 d.querySelector('#close').onclick=()=>d.close();
 if(collection==='customers'){
  const hs=d.querySelector('[name="highsiteId"]'),sec=d.querySelector('[name="sectorId"]');
  const update=()=>{const prior=sec.value;sec.innerHTML='<option value="">Unassigned</option>'+lookupTable('sectors').filter(s=>s.highsite_id===hs.value).map(x=>'<option value="'+esc(x.id)+'">'+esc(x.label)+'</option>').join('');if([...sec.options].some(o=>o.value===prior))sec.value=prior};
  hs.onchange=update;update();
 }
 d.querySelector('#entry').onsubmit=async e=>{
  e.preventDefault();const raw=Object.fromEntries(new FormData(e.target)),value={};
  for(const [key,,type] of fields){
   let v=(raw[key]||'').trim();
   if(type==='number')v=v===''?null:Number(v);
   else if(type==='decimal')v=v===''?null:Number(v);
   else if(['customers','highsites','sectors','packages','branches'].includes(type))v=v||null;
   else if(type==='date')v=v||null;
   value[key]=v;
  }
  try{
   await api('/'+(collection==='nas'?'radius/nas':collection),'POST',value);
   d.close();await refresh();if(collection==='nas')notice('NAS added. Restart FreeRADIUS so it can load the new SQL client.');
  }catch(err){d.querySelector('#error').textContent=err.message}
 };
}
function list(el,collection,title,fields){
 let rows=data[collection]||[],query='';
 el.insertAdjacentHTML('beforeend','<div class="toolbar"><div><h1>'+esc(title)+'</h1><p class="sub">'+rows.length+' records</p></div><div class="buttons"><button id="refresh">Refresh</button>'+(role!=='viewer'&&!(collection==='staff'&&role!=='admin')?'<button class="primary" id="add">+ Add</button>':'')+'</div></div><input id="search" placeholder="Search '+esc(collection)+'..." style="max-width:360px;margin-bottom:14px" aria-label="Search records"><div id="table"></div>');
 const draw=()=>{
  const shown=rows.filter(x=>JSON.stringify(x).toLowerCase().includes(query));
  el.querySelector('#table').innerHTML='<div class="card tablewrap"><table><thead><tr>'+fields.map(k=>'<th>'+esc(k.replaceAll('_',' '))+'</th>').join('')+'</tr></thead><tbody>'+shown.map(x=>'<tr data-id="'+esc(x.id||x.radacctid)+'">'+fields.map(k=>'<td>'+esc(x[k]??'—')+'</td>').join('')+'</tr>').join('')+'</tbody></table>'+(shown.length?'':'<p>No matching records.</p>')+'</div>';
  el.querySelectorAll('#table tr[data-id]').forEach(tr=>tr.onclick=()=>details(collection,rows.find(x=>String(x.id||x.radacctid)===tr.dataset.id)));
 };
 el.querySelector('#search').oninput=e=>{query=e.target.value.toLowerCase();draw()};
 el.querySelector('#refresh').onclick=refresh;
 const add=el.querySelector('#add');if(add)add.onclick=()=>create(collection);
 draw();
}
function details(collection,r){
 if(!r)return;
 let html='<div class="toolbar"><h2>'+esc(r.account||r.name||r.username||r.subject||r.number||'Record')+'</h2><button id="close">Close</button></div><div class="grid">'+Object.entries(r).filter(([k])=>!['password_hash','secret'].includes(k)).map(([k,v])=>'<div class="card"><small>'+esc(k.replaceAll('_',' '))+'</small><div>'+esc(v===null?'—':v)+'</div></div>').join('')+'</div><div class="buttons topgap" id="actions"></div><p id="actionerror" class="error"></p>';
 const d=dialog(html);d.querySelector('#close').onclick=()=>d.close();
 const actions=d.querySelector('#actions');
 const btn=(name,handler)=>{const b=document.createElement('button');b.textContent=name;b.onclick=handler;actions.append(b)};
 const run=async (path,body={})=>{try{await api(path,'POST',body);d.close();await refresh()}catch(e){d.querySelector('#actionerror').textContent=e.message}};
 if(collection==='services'&&role==='admin'){
  btn('Set / rotate RADIUS password',()=>{
   const password=prompt('Enter a NEW subscriber PPPoE password (12–128 characters). This is not stored in your browser.');
   if(password===null)return;if(password.length<12){alert('Password too short');return}
   run('/services/'+r.id+'/credentials',{password});
  });
  btn('Suspend PPPoE authentication',()=>{if(confirm('Disable new PPPoE authentications? Existing sessions require CoA/disconnect.'))run('/services/'+r.id+'/suspend')});
 }
 if(collection==='tickets'&&role!=='viewer'){
  ['in_progress','pending','resolved','closed'].forEach(status=>btn('Mark '+status,async()=>{
   try{await api('/tickets/'+r.id+'/status','PUT',{status});d.close();await refresh()}catch(e){d.querySelector('#actionerror').textContent=e.message}
  }));
 }
 if(collection==='invoices'&&role==='admin'&&r.status==='draft')
  btn('Issue invoice',()=>{if(confirm('Issue invoice '+r.number+'?'))run('/invoices/'+r.id+'/issue')});
 if(collection==='invoices'&&role==='admin'&&r.status==='issued')
  btn('Record payment',()=>{d.close();payment(r)});
}
function payment(invoice){
 const d=dialog('<h2>Record payment · '+esc(invoice.number)+'</h2><form id="pay"><label>Amount (ZAR)</label><input name="amount" type="number" min="0.01" step="0.01" required><label>Reference</label><input name="reference" required><p id="err" class="error"></p><button class="primary">Save payment</button><button type="button" id="cancel">Cancel</button></form>');
 d.querySelector('#cancel').onclick=()=>d.close();
 d.querySelector('#pay').onsubmit=async e=>{e.preventDefault();const raw=Object.fromEntries(new FormData(e.target));
  try{await api('/payments','POST',{invoiceId:invoice.id,amount:Number(raw.amount),reference:raw.reference});d.close();await refresh()}catch(err){d.querySelector('#err').textContent=err.message}};
}
logout.onclick=async()=>{try{await api('/logout','POST');csrf='';await boot()}catch(err){notice(err.message,true)}};
boot();
