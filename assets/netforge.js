// root/assets/netforge.js

'use strict';
// root/index.html — cPanel compatible frontend, no build tools or external dependencies.
const root=document.querySelector('#root'),api='api/index.php';
// Menu groups follow familiar ISP operations organization while keeping NetForge branding.
const navigation=[
 {title:'OVERVIEW',items:[['dashboard','Dashboard','▦'],['wisp','WISP intelligence','◎']]},
 {title:'CRM',items:[['customers','Customers','◉'],['leads','Leads','◇'],['tickets','Tickets','▣'],['finance','Finance','≡']]},
 {title:'NETWORK',items:[['highsites','Highsites','△'],['sectors','Access sectors','◈'],['backhauls','Backhaul links','⇄'],['ipam','IP addresses','#'],['outages','Outages','!']]},
 {title:'FIELD OPERATIONS',items:[['planner','Scheduling','▦'],['workorders','Work orders','☷'],['surveys','RF surveys','⌖'],['maintenance','Maintenance','⚒']]},
 {title:'INVENTORY & SYSTEM',items:[['inventory','Inventory','▤'],['equipment','Deployed equipment','⬡'],['scripts','Scripts','⌘'],['settings','Administration','⚙']]}
];
const routeInfo=Object.fromEntries(navigation.flatMap(g=>g.items.map(x=>[x[0],{label:x[1],group:g.title,icon:x[2]}])));

const collections=['customers','tickets','highsites','equipment','branches','technicians','scripts','staff','packages','sectors','backhauls','outages','maintenance','inventory','leads','changes','ipam','activities','services','invoices','payments','surveys','workorders','link_checks'];
const specs={
 customers:{title:'Customer accounts',fields:[['account','Account name'],['name','First name'],['surname','Surname'],['phone','Phone'],['email','Email'],['address','Address'],['package','Package'],['branch_id','Branch','branches'],['highsite_id','Serving highsite','highsites'],['sector_id','Serving sector','sectors'],['latitude','Latitude'],['longitude','Longitude'],['cpe_ip','CPE IP'],['cpe_username','CPE username'],['notes','Notes','textarea'],["account_status","Account status"],["service_type","Service type"],["installation_date","Installation date","date"],["contract_end","Contract end","date"],["billing_day","Billing day"],["monthly_fee","Monthly fee (ZAR)"],["vlan","VLAN"],["pppoe_username","PPPoE username"],["static_ip","Static IP"],["cpe_mac","CPE MAC"],["signal_dbm","Signal (dBm)"],["last_contact","Last contact","date"],["lead_source","Lead source"],["emergency_contact","Emergency contact"]]},
 tickets:{title:'Support tickets and call logs',fields:[['name','Ticket subject'],['number','Ticket number'],['type','Ticket type','type'],['status','Status','status'],['priority','Priority','priority'],['customer_id','Customer','customers'],['highsite_id','Highsite','highsites'],['branch_id','Branch','branches'],['technician_id','Technician','technicians'],['scheduled_date','Scheduled day','date'],['description','Description','textarea'],['call_log','Call log','textarea'],["source","Contact channel"],["impact","Impact"],["fault_category","Fault category"],["opened_date","Opened date","date"],["due_date","Due date","date"],["resolved_date","Resolved date","date"],["sla_hours","SLA hours"],["resolution","Resolution notes","textarea"],["repeat_fault","Repeat fault"],["contact_name","Contact name"],["contact_phone","Contact phone"]]},
 highsites:{title:'Highsites',fields:[['name','Site name'],['number','Highsite number'],['address','Location / address'],['branch_id','Branch','branches'],['latitude','Latitude'],['longitude','Longitude'],['notes','Notes','textarea'],["site_status","Site status"],["tower_height","Tower height (m)"],["site_owner","Site owner"],["lease_expiry","Lease expiry","date"],["site_contact","Site contact"],["access_instructions","Access instructions","textarea"],["site_code","Site code"],["power_source","Power source"],["battery_capacity","Battery Ah"],["ups_runtime","UPS runtime"],["generator","Generator"],["last_inspection","Last inspection","date"],["next_inspection","Next inspection","date"]]},
 equipment:{title:'Highsite and customer equipment',fields:[['name','Internal name'],['type','Equipment type / model'],['category','Equipment category','category'],['highsite_id','Highsite','highsites'],['customer_id','Customer','customers'],['ownership','Ownership / purchased from ISP'],['serial_number','Serial number'],['ip','Management IP / host'],['username','Login username'],['protocol','Preferred access','protocol'],['port','Port'],['notes','Notes','textarea'],["mac","Mac"],["firmware","Firmware"],["purchase_date","Purchase date","date"],["warranty_expiry","Warranty expiry","date"],["supplier","Supplier"],["purchase_price","Purchase price"],["asset_tag","Asset tag"],["installation_date","Installation date","date"],["last_service","Last service","date"],["next_service","Next service","date"],["condition","Condition"],["poe_type","Poe type"],["vlan","VLAN"]]},
 branches:{title:'Branches',fields:[['name','Branch name'],['address','Address'],['phone','Phone'],["manager","Manager"],["email","Email"],["region","Region"]]},
 technicians:{title:'Technicians',fields:[['name','First name'],['surname','Surname'],['branch_id','Branch','branches'],['phone','Phone'],['active','Active','active'],["skills","Skills"],["employee_number","Employee number"],["email","Email"],["on_call","On call"]]},
 staff:{title:'Login accounts',fields:[['name','Display name'],['username','Login username'],['role','Role','role'],['password','Password (minimum 14 characters)','password']]},
 scripts:{title:'Script library',fields:[['name','Script name'],['language','Language','language'],['risk','Risk level','risk'],['description','Description','textarea'],['body','Script contents','textarea'],["vendor","Vendor"],["version","Version"],["approved","Approved"],["target_scope","Target scope"]]},
 packages:{title:'Packages',fields:[['name','Package name'],['speed','Speed'],['price','Monthly price'],["upload_speed","Upload speed"],["download_speed","Download speed"],["contention","Contention"],["data_policy","Data policy"]]},
 sectors:{title:'Sectors',fields:[['name','Sector name'],['highsite_id','Highsite','highsites'],['azimuth','Azimuth'],['frequency','Frequency'],['type','Radio / sector model'],['ip','Management IP'],['username','Login username'],['protocol','Preferred access','protocol'],['port','Port'],['notes','Notes','textarea'],["ssid","Ssid"],["channel_width","Channel width"],["polarization","Polarization"],["antenna_gain","Antenna gain"],["tx_power","Tx power"],["max_clients","Planned capacity"],["sector_status","Sector status"],["radio_mac","Radio mac"],["tower_height","Tower height (m)"],["beamwidth","Beamwidth"]]},
 backhauls:{title:'Backhauls',fields:[['name','Backhaul name'],['highsite_id','Source highsite','highsites'],['remote_highsite_id','Remote highsite','highsites'],['frequency','Frequency'],['notes','Notes','textarea'],["remote_ip","Remote ip"],["link_distance","Link distance"],["target_rssi","Target rssi"],["channel_width","Channel width"],["link_capacity","Link capacity"],["link_status","Link status"]]},
 outages:{title:"Outages & incidents",fields:[["name","Name"],["number","Number"],["status","Status"],["severity","Severity"],["branch_id","Branch id","branches"],["highsite_id","Highsite id","highsites"],["start_date","Start date","date"],["end_date","End date","date"],["affected_customers","Affected customers"],["root_cause","Root cause","textarea"],["resolution","Resolution notes","textarea"]]},
 maintenance:{title:"Preventive maintenance",fields:[["name","Name"],["highsite_id","Highsite id","highsites"],["equipment_id","Equipment id","equipment"],["branch_id","Branch id","branches"],["technician_id","Technician id","technicians"],["due_date","Due date","date"],["completed_date","Completed date","date"],["status","Status"],["checklist","Checklist","textarea"]]},
 inventory:{title:"Warehouse stock",fields:[["name","Name"],["sku","SKU"],["category","Category"],["branch_id","Branch id","branches"],["quantity","Quantity"],["minimum","Reorder level"],["unit_cost","Unit cost"],["supplier","Supplier"],["location","Location"],["serial_tracking","Serial tracking"]]},
 leads:{title:"Sales leads",fields:[["name","Name"],["phone","Phone"],["email","Email"],["address","Address"],["branch_id","Branch id","branches"],["stage","Stage"],["requested_package","Requested package"],["follow_up","Follow up","date"],["notes","Notes","textarea"]]},
 changes:{title:"Network changes",fields:[["name","Name"],["change_type","Change type"],["risk","Risk"],["status","Status"],["highsite_id","Highsite id","highsites"],["technician_id","Technician id","technicians"],["planned_date","Planned date","date"],["rollback_plan","Rollback plan","textarea"],["description","Description","textarea"]]},
 ipam:{title:"IP address register",fields:[["name","Name"],["address","Address"],["vlan","VLAN"],["branch_id","Branch id","branches"],["highsite_id","Highsite id","highsites"],["customer_id","Customer id","customers"],["purpose","Purpose"],["status","Status"],["notes","Notes","textarea"]]},
 activities:{title:'Customer communications',fields:[['name','Subject'],['customer_id','Customer','customers'],['ticket_id','Ticket','tickets'],['type','Interaction type','activitytype'],['channel','Channel','source'],['description','Notes','textarea'],['occurred_at','Occurred at'],['performed_by','Performed by']]},
 services:{title:'Customer services',fields:[['name','Service label'],['customer_id','Customer','customers'],['service_type','Service type','servicetype'],['status','Status','servicestatus'],['package','Package'],['highsite_id','Highsite','highsites'],['sector_id','Sector','sectors'],['vlan','VLAN'],['ip','IP address'],['pppoe_username','PPPoE username'],['activated_date','Activated date','date'],['notes','Service notes','textarea']]},
 invoices:{title:'Invoices — manual register',fields:[['name','Invoice description'],['number','Invoice number'],['customer_id','Customer','customers'],['branch_id','Branch','branches'],['invoice_date','Invoice date','date'],['due_date','Due date','date'],['amount','Amount (ZAR)'],['status','Status','invoicestatus'],['description','Notes','textarea']]},
 payments:{title:'Payments — manual register',fields:[['name','Payment description'],['reference','Payment reference'],['customer_id','Customer','customers'],['invoice_id','Invoice','invoices'],['payment_date','Payment date','date'],['amount','Amount (ZAR)'],['method','Payment method','paymentmethod'],['notes','Notes','textarea']]},
 surveys:{title:'WISP RF surveys',fields:[['name','Survey name'],['customer_id','Customer','customers'],['lead_id','Lead','leads'],['branch_id','Branch','branches'],['highsite_id','Candidate highsite','highsites'],['latitude','Latitude'],['longitude','Longitude'],['azimuth','Azimuth'],['distance_km','Distance (km)'],['fresnel_clearance','Fresnel clearance (%)'],['los_status','Line of sight','losstatus'],['status','Survey status','surveystatus'],['notes','Survey notes','textarea']]},
 workorders:{title:'Technician work orders',fields:[['name','Job description'],['ticket_id','Ticket','tickets'],['customer_id','Customer','customers'],['highsite_id','Highsite','highsites'],['technician_id','Technician','technicians'],['branch_id','Branch','branches'],['scheduled_date','Scheduled day','date'],['status','Status','workstatus'],['checklist','Checklist','textarea'],['materials','Materials used','textarea'],['completion_notes','Completion notes','textarea']]},
 link_checks:{title:'Manual RF diagnostics',fields:[['name','Diagnostic label'],['customer_id','Customer','customers'],['sector_id','Sector','sectors'],['rssi_dbm','Receive signal (dBm)'],['snr_db','SNR (dB)'],['ccq','CCQ (%)'],['tx_mbps','TX (Mbps)'],['rx_mbps','RX (Mbps)'],['captured_at','Captured at'],['notes','Notes','textarea']]}

};
const options={type:['customer','highsite','voip','other'],status:['open','in_progress','pending','resolved','closed'],priority:['low','normal','high','critical'],category:['distribution','backhaul','access','power','cpe','router','other'],protocol:['https','http','ssh','winbox'],role:['admin','dispatcher','technician','viewer'],language:['routeros','python','bash','ssh'],risk:['read-only','change','high-risk'],active:['yes','no'],activitytype:['call','note','email','site-visit','whatsapp'],servicetype:['wireless','fibre','voip','other'],servicestatus:['planned','active','suspended','cancelled'],invoicestatus:['draft','issued','part-paid','paid','void'],paymentmethod:['eft','cash','card','debit-order','other'],losstatus:['clear','partial','obstructed','unknown'],surveystatus:['requested','scheduled','completed','not-feasible'],workstatus:['open','assigned','in-progress','blocked','completed'],account_status:['active','pending','suspended','cancelled'],service_type:['wireless','fibre','voip','other'],site_status:['active','planned','offline','decommissioned'],power_source:['grid','solar','hybrid','generator'],generator:['yes','no'],repeat_fault:['yes','no'],source:['phone','email','whatsapp','portal','walk-in','monitoring'],impact:['single-customer','multiple-customers','site-wide'],fault_category:['no-connectivity','slow-speed','intermittent','hardware','billing','voip','other'],ownership:['customer-owned','isp-owned','rented','loaned'],condition:['new','good','fair','faulty','retired'],on_call:['yes','no'],approved:['yes','no'],target_scope:['customer','sector','highsite','all-sites'],severity:['minor','major','critical'],serial_tracking:['yes','no'],stage:['new','contacted','survey','quoted','won','lost'],change_type:['planned','emergency','maintenance']};
let state={view:'dashboard',cache:{},csrf:'',role:'',month:new Date(new Date().getFullYear(),new Date().getMonth(),1),branch:''};
const esc=v=>String(v??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;','\'':'&#39;'}[c]));
async function call(params={},method='GET',data){let o={method,credentials:'same-origin',headers:{}};if(method!=='GET')o.headers['X-CSRF-Token']=state.csrf;if(data!==undefined){o.headers['Content-Type']='application/json';o.body=JSON.stringify(data)}let r=await fetch(api+'?'+new URLSearchParams(params),o);let x=await r.json();if(!r.ok)throw Error(x.error||'Request failed');return x}
const all=c=>state.cache[c]||[];
const lookup=(c,id)=>{const x=all(c).find(x=>x.id===id);return x?(c==='customers'?(x.account||x.name||x.id):(x.name||x.number||x.id)):id||'—'};
const branchName=id=>lookup('branches',id);
function toast(s){const el=document.querySelector('#flash');if(el)el.textContent=s;else alert(s)}
async function boot(){try{let s=await call({action:'status'});state.csrf=s.csrf||'';state.role=s.role||'';if(!s.authenticated)return login(s.installed);document.querySelector('#logout').hidden=false;document.querySelector('#identity').textContent=s.username||'';await refresh()}catch(e){root.innerHTML='<main><div class="notice">'+esc(e.message)+'</div></main>'}}
function login(installed){document.querySelector('#logout').hidden=true;root.innerHTML='<main><div class="card login"><h2>'+(installed?'Sign in':'Create administrator')+'</h2><form id="login"><label>Username</label><input name="username" required autocomplete="username"><label>Password</label><input name="password" type="password" required minlength="'+(installed?1:14)+'" autocomplete="'+(installed?'current-password':'new-password')+'"><p id="flash"></p><button class="primary">Continue</button></form></div></main>';document.querySelector('#login').onsubmit=async e=>{e.preventDefault();try{let r=await call({action:installed?'login':'setup'},'POST',Object.fromEntries(new FormData(e.target)));state.csrf=r.csrf;await boot()}catch(x){toast(x.message)}}}
async function refresh(){
 root.innerHTML='<div class="shell"><nav id="nav" aria-label="Primary navigation"></nav><main id="content"><div class="nf-skeleton">Loading NetForge records…</div></main></div>';
 navigation.forEach(group=>{
  const section=document.createElement('div');section.className='nf-nav-section';
  const heading=document.createElement('div');heading.className='nf-nav-heading';heading.textContent=group.title;section.append(heading);
  group.items.forEach(([key,label,icon])=>{const btn=document.createElement('button');btn.type='button';btn.dataset.view=key;btn.className='nf-nav-link';btn.innerHTML='<span class="nf-nav-icon">'+esc(icon)+'</span><span>'+esc(label)+'</span>';btn.onclick=()=>go(key);section.append(btn)});
  document.querySelector('#nav').append(section);
 });
 let errors=[];
 await Promise.all(collections.map(async c=>{
  if(c==='staff'&&state.role!=='admin'){state.cache[c]=[];return}
  try{state.cache[c]=await call({category:c})}catch(e){state.cache[c]=[];errors.push(c+': '+e.message)}
 }));
 render();
 if(errors.length)toast('Some records could not be loaded: '+errors.join('; '));
}
function go(view){state.view=view;render();document.querySelector('main')?.scrollIntoView({behavior:'instant',block:'start'})}
function render(){
 document.querySelectorAll('#nav button').forEach(b=>{b.classList.toggle('active',b.dataset.view===state.view);b.setAttribute('aria-current',b.dataset.view===state.view?'page':'false')});
 let view=state.view,meta=routeInfo[view]||{group:'WORKSPACE',label:'NetForge'};
 let el=document.querySelector('#content');
 el.innerHTML='<div class="nf-breadcrumb">'+esc(meta.group)+' <span>›</span> '+esc(meta.label)+'</div><p id="flash" role="status"></p>';
 if(view==='dashboard')dashboard();
 else if(view==='wisp')wisp();
 else if(view==='customers')list('customers',['account','name','surname','package','highsite_id','sector_id','account_status']);
 else if(view==='tickets')list('tickets',['number','name','type','priority','status','technician_id','scheduled_date']);
 else if(view==='leads')list('leads',['name','phone','stage','follow_up','branch_id']);
 else if(view==='highsites')list('highsites',['number','name','site_status','branch_id','power_source']);
 else if(view==='sectors')list('sectors',['name','highsite_id','azimuth','frequency','max_clients','sector_status']);
 else if(view==='backhauls')list('backhauls',['name','highsite_id','remote_highsite_id','frequency','link_capacity','link_status']);
 else if(view==='ipam')list('ipam',['name','address','vlan','purpose','status']);
 else if(view==='outages')list('outages',['number','name','severity','status','highsite_id','start_date']);
 else if(view==='workorders')list('workorders',['name','customer_id','technician_id','branch_id','scheduled_date','status']);
 else if(view==='surveys')list('surveys',['name','highsite_id','los_status','distance_km','status']);
 else if(view==='maintenance')list('maintenance',['name','highsite_id','technician_id','due_date','status']);
 else if(view==='inventory')list('inventory',['sku','name','quantity','minimum','branch_id']);
 else if(view==='equipment')list('equipment',['name','type','ip','category','highsite_id','customer_id']);
 else if(view==='finance')finance();
 else if(view==='scripts')scripts();
 else if(view==='planner')planner();
 else if(view==='settings')settings();
 else el.insertAdjacentHTML('beforeend','<p class="muted">Unknown page.</p>');
}
function resolvedValue(k,r){
 let val=r[k]??'';
 if(k.endsWith('_id')){const c={branch_id:'branches',technician_id:'technicians',highsite_id:'highsites',remote_highsite_id:'highsites',customer_id:'customers',sector_id:'sectors',lead_id:'leads',ticket_id:'tickets',invoice_id:'invoices',equipment_id:'equipment',outage_id:'outages'}[k];if(c)return val?lookup(c,val):'—'}
 return val||'—';
}
function cell(c,k,r){
 let val=resolvedValue(k,r);
 if(['status','priority','severity','account_status','site_status','sector_status','link_status','stage','los_status'].includes(k)){
 let v=String(val).toLowerCase(),tone=/critical|offline|overdue|obstructed|suspended|cancelled|faulty/.test(v)?'bad':/pending|warning|planned|partial|high|blocked|investigating/.test(v)?'warn':/active|paid|resolved|completed|clear|good|won/.test(v)?'good':'neutral';
 return '<span class="nf-status nf-'+tone+'">'+esc(val)+'</span>';
 }
 return esc(val);
}
function table(c,keys,rows){
 return '<div class="card tablewrap"><table><thead><tr>'+keys.map(k=>'<th>'+esc(k.replace(/_id$/,'').replaceAll('_',' '))+'</th>').join('')+'</tr></thead><tbody>'+
 rows.map(r=>'<tr tabindex="0" data-id="'+esc(r.id)+'">'+keys.map(k=>'<td>'+cell(c,k,r)+'</td>').join('')+'</tr>').join('')+'</tbody></table>'+
 (rows.length?'':'<p class="nf-empty" style="padding:25px">No records match the current filter.</p>')+'</div>';
}
function list(c,keys,filter){
 const base=all(c).filter(filter||(()=>true));let page=0,sortKey='',reverse=false;const pageSize=25;
 const canEdit=state.role!=='viewer'&&(c!=='staff'||state.role==='admin');
 const el=document.querySelector('#content');
 el.insertAdjacentHTML('beforeend','<div class="nf-pagehead"><div><h2>'+esc(specs[c].title)+'</h2><div class="nf-subtitle">'+base.length+' records · filter, sort and open to view details</div></div><div class="tools"><button id="list-export">Export CSV</button>'+(canEdit?'<button class="primary" id="add">+ New record</button>':'')+'</div></div><div class="nf-filters"><input id="search" aria-label="Search records" placeholder="Search '+esc(c)+'..." style="width:min(360px,100%)"><select id="sort" aria-label="Sort records" style="width:190px"><option value="">Original order</option>'+keys.map(k=>'<option value="'+esc(k)+'">'+esc(k.replaceAll('_',' '))+'</option>').join('')+'</select><select id="status-filter" style="width:175px" aria-label="Filter status"><option value="">All statuses</option>'+[...new Set(base.map(x=>x.account_status||x.status||x.site_status||x.sector_status||x.stage||'').filter(Boolean))].sort().map(x=>'<option value="'+esc(x)+'">'+esc(x)+'</option>').join('')+'</select></div><div id="listing"></div><div id="list-pagination" class="nf-pagination"></div>');
 const draw=()=>{
   let query=document.querySelector('#search').value.trim().toLowerCase(),status=document.querySelector('#status-filter').value;
   let rows=base.filter(r=>{let matches=!query||Object.values(r).some(v=>typeof v==='string'&&v.toLowerCase().includes(query));return matches&&(!status||[r.status,r.account_status,r.site_status,r.sector_status,r.stage].includes(status))});
   let sort=document.querySelector('#sort').value;if(sort)rows.sort((a,b)=>String(resolvedValue(sort,a)).localeCompare(String(resolvedValue(sort,b)),undefined,{numeric:true,sensitivity:'base'}));
   let pages=Math.max(1,Math.ceil(rows.length/pageSize));page=Math.min(page,pages-1);
   document.querySelector('#listing').innerHTML=table(c,keys,rows.slice(page*pageSize,page*pageSize+pageSize));
   document.querySelector('#listing').querySelectorAll('tr[data-id]').forEach(tr=>{const open=()=>detail(c,tr.dataset.id);tr.onclick=open;tr.onkeydown=e=>{if(e.key==='Enter')open()}});
   const p=document.querySelector('#list-pagination');p.innerHTML='<span>'+rows.length+' results · page '+(page+1)+' of '+pages+'</span><div class="tools"><button id="prev-page" '+(page===0?'disabled':'')+'>Previous</button><button id="next-page" '+(page>=pages-1?'disabled':'')+'>Next</button></div>';
   p.querySelector('#prev-page').onclick=()=>{page--;draw()};p.querySelector('#next-page').onclick=()=>{page++;draw()};
 };
 document.querySelector('#search').oninput=()=>{page=0;draw()};
 document.querySelector('#sort').onchange=()=>{page=0;draw()};
 document.querySelector('#status-filter').onchange=()=>{page=0;draw()};
 document.querySelector('#list-export').onclick=()=>exportCollection(c);
 if(canEdit)document.querySelector('#add').onclick=()=>form(c);
 draw();
}
function fieldHTML(c,f,r={}){let [key,label,kind]=f,v=r[key]??'';let id='f_'+key;let input;if(kind==='textarea')input='<textarea rows="'+(key==='body'?10:3)+'" id="'+id+'" name="'+key+'">'+esc(v)+'</textarea>';else if(kind==='password')input='<input type="password" id="'+id+'" name="'+key+'" minlength="14" required>';else if(kind==='date')input='<input type="date" id="'+id+'" name="'+key+'" value="'+esc(v)+'">';else if(options[kind])input='<select id="'+id+'" name="'+key+'">'+options[kind].map(x=>'<option value="'+esc(x)+'" '+(v===x?'selected':'')+'>'+esc(x)+'</option>').join('')+'</select>';else if(specs[kind])input='<select id="'+id+'" name="'+key+'"><option value="">Unassigned</option>'+all(kind).filter(x=>!(c==='customers'&&key==='sector_id')||x.highsite_id===r.highsite_id).map(x=>'<option value="'+esc(x.id)+'" '+(v===x.id?'selected':'')+'>'+esc(x.name||x.account)+'</option>').join('')+'</select>';else input='<input id="'+id+'" name="'+key+'" value="'+esc(v)+'" maxlength="300">';return '<div class="'+(['textarea'].includes(kind)?'wide':'')+'"><label for="'+id+'">'+esc(label)+'</label>'+input+'</div>'}
function dialog(html){let d=document.createElement('dialog');d.innerHTML=html;document.body.append(d);d.onclose=()=>d.remove();d.showModal();return d}
function form(c,existing=null,defaults={}){let record=existing||defaults,edit=!!existing;let d=dialog('<form id="entry"><div class="toolbar"><h2>'+(edit?'Edit ':'Add ')+esc(c)+'</h2><button type="button" id="close">Close</button></div><div class="formgrid">'+specs[c].fields.map(f=>fieldHTML(c,f,record)).join('')+'</div><p id="formerror"></p><div class="toolbar"><button class="primary" type="submit">Save</button>'+(edit?'<button type="button" id="delete">Delete</button>':'')+'</div></form>');d.querySelector('#close').onclick=()=>d.close();
if(c==='customers'||c==='services'){
 const hs=d.querySelector('[name="highsite_id"]'),sector=d.querySelector('[name="sector_id"]');
 const sync=()=>{let previous=sector.value;sector.innerHTML='<option value="">Unassigned</option>'+all('sectors').filter(x=>x.highsite_id===hs.value).map(x=>'<option value="'+esc(x.id)+'">'+esc(x.name)+'</option>').join('');sector.value=Array.from(sector.options).some(x=>x.value===previous)?previous:'';};
 hs.addEventListener('change',sync);sync();
}
d.querySelector('#entry').onsubmit=async e=>{e.preventDefault();let data=Object.fromEntries(new FormData(e.target));if(edit){data.id=existing.id;data.revision=existing.revision??1;}try{await call({category:c},edit?'PUT':'POST',data);d.close();await refresh()}catch(x){d.querySelector('#formerror').textContent=x.message}};if(edit)d.querySelector('#delete').onclick=async()=>{if(!confirm('Delete record? This may leave references in other records.'))return;try{await call({category:c,id:existing.id},'DELETE');d.close();await refresh()}catch(x){d.querySelector('#formerror').textContent=x.message}}}
function detail(c,id){if(c==='customers')return customer360(id);if(c==='highsites')return site360(id);let r=all(c).find(x=>x.id===id);if(!r)return;let d=dialog('<div class="toolbar"><h2>'+esc(r.name||r.account)+'</h2><div class="tools"><button id="edit">Edit</button><button id="close">Close</button></div></div><div id="details"></div>');d.querySelector('#close').onclick=()=>d.close();d.querySelector('#edit').onclick=()=>{d.close();form(c,r)};let area=d.querySelector('#details');area.innerHTML='<div class="grid">'+specs[c].fields.filter(x=>x[0]!=='body').map(([k,l])=>'<div class="card"><small>'+esc(l)+'</small><div>'+esc(k.endsWith('_id')?lookup(k==='branch_id'?'branches':k==='sector_id'?'sectors':k==='technician_id'?'technicians':k==='customer_id'?'customers':'highsites',r[k]):r[k]||'—')+'</div></div>').join('')+'</div>';if(c==='customers'){let lat=Number(r.latitude),lon=Number(r.longitude);area.insertAdjacentHTML('beforeend','<div class="section grid"><div class="card"><h3>Customer location</h3><div class="map">'+(Number.isFinite(lat)&&Number.isFinite(lon)&&r.latitude&&r.longitude?'<a target="_blank" rel="noopener noreferrer" style="color:#a9d0ff" href="https://www.openstreetmap.org/?mlat='+encodeURIComponent(lat)+'&mlon='+encodeURIComponent(lon)+'#map=15/'+encodeURIComponent(lat)+'/'+encodeURIComponent(lon)+'">Open map at '+esc(lat)+', '+esc(lon)+'</a>':'Coordinates not configured')+'</div></div><div class="card"><h3>Equipment access</h3><p>Assigned sector: '+esc(lookup('sectors',r.sector_id))+'</p><p>CPE IP: '+esc(r.cpe_ip||'Not configured')+'</p><button id="customer-ticket">Create ticket / log call</button></div></div>');area.querySelector('#customer-ticket').onclick=()=>{d.close();form('tickets',null,{customer_id:r.id,type:'customer',status:'open',priority:'normal',name:'Customer support call',branch_id:r.branch_id})};
area.insertAdjacentHTML('beforeend','<div class="section card"><div class="toolbar"><h3>Customer equipment</h3><button id="customer-add-equipment">+ Add equipment</button></div><div class="tabs" id="customer-equip-tabs"></div><div id="customer-equip-list"></div></div>');
let customerEquipmentTab='cpe';
const renderCustomerEquipment=()=>{let types=['cpe','router','other'];area.querySelector('#customer-equip-tabs').innerHTML=types.map(x=>'<button data-equip-tab="'+x+'" class="'+(x===customerEquipmentTab?'active':'')+'">'+(x==='cpe'?'Antenna / CPE':x==='router'?'Internal routers':'Other purchased equipment')+'</button>').join('');area.querySelectorAll('[data-equip-tab]').forEach(b=>b.onclick=()=>{customerEquipmentTab=b.dataset.equipTab;renderCustomerEquipment()});let devices=all('equipment').filter(x=>x.customer_id===r.id&&x.category===customerEquipmentTab);area.querySelector('#customer-equip-list').innerHTML=devices.length?devices.map(x=>'<div class="card" style="margin:8px 0"><div class="toolbar"><div><b>'+esc(x.name)+'</b><br><small>'+esc(x.type)+' · '+esc(x.ip||'No IP')+' · '+esc(x.serial_number||'No serial')+'</small><br><small>'+esc(x.ownership||'Ownership not specified')+'</small></div><div class="tools"><button data-edit-cpe="'+esc(x.id)+'">Edit</button><button data-connect-cpe="'+esc(x.id)+'">Connect</button></div></div></div>').join(''):'<p class="muted">No equipment in this category.</p>';area.querySelectorAll('[data-edit-cpe]').forEach(b=>b.onclick=()=>{d.close();form('equipment',all('equipment').find(x=>x.id===b.dataset.editCpe))});area.querySelectorAll('[data-connect-cpe]').forEach(b=>b.onclick=()=>access(all('equipment').find(x=>x.id===b.dataset.connectCpe)))};area.querySelector('#customer-add-equipment').onclick=()=>{d.close();form('equipment',null,{customer_id:r.id,category:customerEquipmentTab,protocol:'https',ownership:'Purchased from ISP'})};renderCustomerEquipment();
}
if(c==='highsites'){area.insertAdjacentHTML('beforeend','<div class="section"><h3>Site equipment</h3><div class="tabs" id="site-tabs"></div><div id="site-equipment"></div></div>');let current='distribution';const draw=()=>{area.querySelector('#site-tabs').innerHTML=['distribution','backhaul','access','power'].map(x=>'<button data-tab="'+x+'" class="'+(x===current?'active':'')+'">'+x+'</button>').join('');area.querySelectorAll('#site-tabs button').forEach(b=>b.onclick=()=>{current=b.dataset.tab;draw()});let devices=current==='access'?all('sectors').filter(x=>x.highsite_id===r.id):all('equipment').filter(x=>x.highsite_id===r.id&&x.category===current);area.querySelector('#site-equipment').innerHTML='<div class="toolbar"><span>'+devices.length+(current==='access'?' sectors':' devices')+'</span><button id="new-device">+ Add '+(current==='access'?'sector':'equipment')+'</button></div>'+devices.map(x=>'<div class="card" style="margin-bottom:8px"><div class="toolbar"><div><b>'+esc(x.name)+'</b><br><small>'+esc(x.type)+' · '+esc(x.ip)+'</small></div><div class="tools"><button data-edit="'+esc(x.id)+'">Edit</button><button data-access="'+esc(x.id)+'">Connect</button></div></div></div>').join('');area.querySelector('#new-device').onclick=()=>{d.close();form(current==='access'?'sectors':'equipment',null,{highsite_id:r.id,category:current,protocol:'https'})};area.querySelectorAll('[data-edit]').forEach(b=>b.onclick=()=>{d.close();form(current==='access'?'sectors':'equipment',all(current==='access'?'sectors':'equipment').find(x=>x.id===b.dataset.edit))});area.querySelectorAll('[data-access]').forEach(b=>b.onclick=()=>access(all(current==='access'?'sectors':'equipment').find(x=>x.id===b.dataset.access)))};draw()}}
function access(x){if(!x)return;let ip=x.ip||'';if(!/^[a-zA-Z0-9.:-]+$/.test(ip)){alert('Invalid device hostname/IP');return}let proto=x.protocol||'https';if(proto==='https'||proto==='http'){let port=x.port&&/^\d{1,5}$/.test(x.port)?':'+x.port:'';window.open(proto+'://'+ip+port,'_blank','noopener,noreferrer')}else if(proto==='ssh'){alert('Use your local SSH client: ssh '+(x.username||'admin')+'@'+ip+'\nBrowser-hosted NetForge cannot open SSH sessions directly.')}else{alert('Use Winbox locally and connect to '+ip+'\nBrowsers cannot directly launch Winbox reliably.')} }
function scripts(){list('scripts',['name','language','risk','description']);document.querySelector('#content').insertAdjacentHTML('beforeend','<div class="notice">Scripts are stored as text only. Execution, targeting, scheduling and credential access remain disabled until a trusted automation agent is connected.</div>')}
function settings(){let el=document.querySelector('#content');el.insertAdjacentHTML('beforeend','<h2>Settings</h2><div class="tabs" id="settings-tabs"></div><div id="settings-body"></div>');let active='branches';function draw(){document.querySelector('#settings-tabs').innerHTML=['branches','technicians',...(state.role==='admin'?['staff']:[]),'packages','sectors'].map(c=>'<button data-c="'+c+'" class="'+(active===c?'active':'')+'">'+esc(specs[c].title)+'</button>').join('');document.querySelectorAll('#settings-tabs button').forEach(b=>b.onclick=()=>{active=b.dataset.c;draw()});let rows=all(active);document.querySelector('#settings-body').innerHTML='<div class="toolbar"><span>'+rows.length+' records</span><button class="primary" id="settings-add">+ Add</button></div>'+table(active,active==='staff'?['name','username','role']:['name',...(active==='technicians'?['surname','branch_id']:active==='sectors'?['highsite_id','azimuth']:['address'])],rows);document.querySelector('#settings-add').onclick=()=>form(active);document.querySelectorAll('#settings-body tr[data-id]').forEach(tr=>tr.onclick=()=>{if(active==='staff'){alert('Staff password changes are not supported in this release.');return}form(active,rows.find(x=>x.id===tr.dataset.id))})}draw()}
function dateKey(d){return d.getFullYear()+'-'+String(d.getMonth()+1).padStart(2,'0')+'-'+String(d.getDate()).padStart(2,'0')}

function planner(){
 const el=document.querySelector('#content'),m=state.month,title=m.toLocaleDateString(undefined,{month:'long',year:'numeric'});
 const filt=x=>!state.branch||x.branch_id===state.branch;
 const tickets=all('tickets').filter(filt).map(x=>({kind:'tickets',record:x}));
 const orders=all('workorders').filter(filt).map(x=>({kind:'workorders',record:x}));
 const tasks=[...tickets,...orders];
 const techs=all('technicians').filter(filt);
 const unscheduled=tasks.filter(x=>!x.record.scheduled_date);
 const workload=techs.map(t=>({name:t.name+' '+(t.surname||''),count:tasks.filter(x=>x.record.technician_id===t.id&&x.record.scheduled_date?.startsWith(m.getFullYear()+'-'+String(m.getMonth()+1).padStart(2,'0'))).length}));
 el.insertAdjacentHTML('beforeend','<div class="nf-kicker">Field service</div><div class="toolbar"><div><h2>Technician scheduling · '+esc(title)+'</h2><p class="nf-detail-subtitle">Drag tickets or work orders to a day and assign a branch technician. Scheduling does not send notifications automatically.</p></div><div class="tools"><select id="branch-filter"><option value="">All branches</option>'+all('branches').map(x=>'<option value="'+esc(x.id)+'" '+(state.branch===x.id?'selected':'')+'>'+esc(x.name)+'</option>').join('')+'</select><button id="prev">← Previous</button><button id="next">Next →</button></div></div><div class="grid" style="margin-bottom:15px"><div class="card"><b>Unscheduled queue</b><div class="metric">'+unscheduled.length+'</div></div><div class="card"><b>Tickets</b><div class="metric">'+tickets.length+'</div></div><div class="card"><b>Work orders</b><div class="metric">'+orders.length+'</div></div><div class="card"><b>Technicians</b><div class="metric">'+techs.length+'</div></div></div><div id="unscheduled" class="card" style="margin-bottom:15px"></div><div class="calendar" id="calendar"></div><div class="card section"><h3>Monthly technician workload</h3><div id="tech-workload"></div></div>');
 document.querySelector('#branch-filter').onchange=e=>{state.branch=e.target.value;render()};
 document.querySelector('#prev').onclick=()=>{state.month=new Date(m.getFullYear(),m.getMonth()-1,1);render()};
 document.querySelector('#next').onclick=()=>{state.month=new Date(m.getFullYear(),m.getMonth()+1,1);render()};
 document.querySelector('#unscheduled').innerHTML='<div class="toolbar"><b>Unscheduled work ('+unscheduled.length+')</b><div class="tools"><button id="quick-ticket">+ Ticket</button><button id="quick-workorder">+ Work order</button></div></div><div class="grid">'+unscheduled.map(taskCard).join('')+'</div>';
 document.querySelector('#quick-ticket').onclick=()=>form('tickets',null,{type:'customer',status:'open',priority:'normal'});
 document.querySelector('#quick-workorder').onclick=()=>form('workorders',null,{status:'open',branch_id:state.branch});
 const first=new Date(m.getFullYear(),m.getMonth(),1),offset=(first.getDay()+6)%7,days=new Date(m.getFullYear(),m.getMonth()+1,0).getDate(),cal=document.querySelector('#calendar');
 for(let i=0;i<offset;i++)cal.insertAdjacentHTML('beforeend','<div aria-hidden="true"></div>');
 for(let n=1;n<=days;n++){
  let date=new Date(m.getFullYear(),m.getMonth(),n),key=dateKey(date),items=tasks.filter(x=>x.record.scheduled_date===key);
  const day=document.createElement('div');day.className='day'+(key===dateKey(new Date())?' today':'');day.dataset.day=key;
  day.innerHTML='<div class="dayhead">'+esc(date.toLocaleDateString(undefined,{weekday:'short',day:'numeric'}))+' <span style="float:right">'+items.length+'</span></div>'+items.map(taskCard).join('');
  day.ondragover=e=>{e.preventDefault();day.classList.add('dragover')};
  day.ondragleave=()=>day.classList.remove('dragover');
  day.ondrop=e=>{e.preventDefault();day.classList.remove('dragover');schedule(e.dataTransfer.getData('text/plain'),key)};
  cal.append(day);
 }
 document.querySelector('#tech-workload').innerHTML=workload.length?workload.map(x=>'<div class="nf-statline"><span>'+esc(x.name)+'</span><b>'+x.count+' assigned</b></div>').join(''):'<p class="nf-empty">Add technicians in Administration to manage assignments.</p>';
 document.querySelectorAll('.ticket[data-task]').forEach(b=>{
  b.ondragstart=e=>e.dataTransfer.setData('text/plain',b.dataset.task);
  b.onclick=()=>{let [kind,id]=b.dataset.task.split(':');detail(kind,id)};
 });
}
function taskCard(task){
 const t=task.record,label=task.kind==='tickets'?'Ticket':'Work order';
 return '<div class="ticket" draggable="true" data-task="'+task.kind+':'+esc(t.id)+'"><small>'+label+'</small><b>'+esc(t.number||t.name)+'</b><small>'+esc(lookup('technicians',t.technician_id))+'</small></div>';
}
function schedule(spec,date){
 const match=/^(tickets|workorders):([a-f0-9]{32})$/.exec(spec);if(!match)return;
 const [,kind,id]=match,record=all(kind).find(x=>x.id===id);if(!record)return;
 const candidates=all('technicians').filter(x=>(!record.branch_id||x.branch_id===record.branch_id)&&x.active!=='no');
 if(!candidates.length){toast('No active technicians are assigned to this branch.');return}
 const choices=candidates.map((x,i)=>(i+1)+'. '+x.name+' '+(x.surname||'')).join('\n');
 const selection=prompt('Assign technician to '+date+' (number):\n'+choices,String(Math.max(1,candidates.findIndex(x=>x.id===record.technician_id)+1)));
 if(selection===null)return;
 const selected=candidates[Number(selection)-1];if(!selected){toast('Invalid technician selection');return}
 call({category:kind},'PUT',{...record,technician_id:selected.id,scheduled_date:date}).then(refresh).catch(e=>toast(e.message));
}
function dashboard(){
const today=dateKey(new Date()),tickets=all('tickets'),customers=all('customers'),sectors=all('sectors'),outages=all('outages');
const open=tickets.filter(x=>!['closed','resolved'].includes(x.status));
const overdue=open.filter(x=>x.due_date&&x.due_date<today);
const due=all('maintenance').filter(x=>x.due_date&&x.due_date<=today&&x.status!=='completed');
const low=all('inventory').filter(x=>Number(x.quantity)<Number(x.minimum));
const cards=[['Total customers',customers.length,''],['Open tickets',open.length,''],['Overdue tickets',overdue.length,'nf-danger'],['Highsites',all('highsites').length,''],['Active outages',outages.filter(x=>x.status!=='resolved').length,'nf-warning'],['Maintenance due',due.length,'nf-warning'],['Low stock alerts',low.length,'nf-warning'],['Pending leads',all('leads').filter(x=>!['won','lost'].includes(x.stage)).length,'']];
let el=document.querySelector('#content');
el.insertAdjacentHTML('beforeend','<div class="nf-kicker">Network command centre</div><div class="nf-topline"><div><h2>Operations overview</h2><div class="nf-subtitle">Live summary of locally stored NetForge records · '+esc(today)+'</div></div><button id="dash-refresh">Refresh data</button></div><div class="grid">'+cards.map(([k,v,cl])=>'<div class="card nf-metric '+cl+'"><small>'+esc(k)+'</small><div class="metric">'+v+'</div></div>').join('')+'</div><div class="grid section" style="grid-template-columns:repeat(auto-fit,minmax(300px,1fr))"><div class="card"><div class="nf-section-title">Priority ticket queue</div><div id="dash-tickets"></div></div><div class="card"><div class="nf-section-title">Sector capacity</div><div id="dash-sectors"></div></div><div class="card"><div class="nf-section-title">Operations alerts</div><div id="dash-alerts"></div></div></div>');
document.querySelector('#dash-refresh').onclick=refresh;
const urgent=[...open].sort((a,b)=>(a.due_date||'9999').localeCompare(b.due_date||'9999')).slice(0,7);
document.querySelector('#dash-tickets').innerHTML=urgent.length?'<div class="nf-list">'+urgent.map(x=>'<div class="nf-item"><div><b>'+esc(x.name||x.number)+'</b><div class="nf-count">'+esc(x.type||'Ticket')+' · '+esc(x.due_date||'No due date')+'</div></div><span class="nf-badge">'+esc(x.priority||'normal')+'</span></div>').join('')+'</div>':'<p class="nf-empty">No open tickets.</p>';
document.querySelector('#dash-sectors').innerHTML=sectors.length?sectors.slice(0,9).map(x=>{let n=customers.filter(c=>c.sector_id===x.id).length,cap=Math.max(0,Number(x.max_clients)||0),pct=cap?Math.min(100,Math.round(n/cap*100)):0;return '<div class="nf-item" style="display:block"><div style="display:flex;justify-content:space-between"><b>'+esc(x.name)+'</b><span class="nf-count">'+n+(cap?' / '+cap:' customers')+'</span></div>'+(cap?'<div class="nf-progress"><span style="width:'+pct+'%;background:'+(pct>=90?'#dc7272':'#009d91')+'"></span></div>':'')+'</div>'}).join(''):'<p class="nf-empty">No sectors configured.</p>';
const alerts=[...overdue.map(x=>({name:'Overdue: '+(x.name||x.number),type:'Ticket'})),...due.map(x=>({name:x.name,type:'Maintenance'})),...low.map(x=>({name:x.name,type:'Low stock'}))].slice(0,9);
document.querySelector('#dash-alerts').innerHTML=alerts.length?alerts.map(x=>'<div class="nf-item"><span>'+esc(x.name)+'</span><span class="nf-badge">'+esc(x.type)+'</span></div>').join(''):'<p class="nf-empty">No outstanding alerts from the recorded data.</p>';
}
function exportCollection(c){let rows=all(c),keys=['id',...specs[c].fields.map(x=>x[0]).filter(x=>x!=='password')];let q=v=>{let x=String(v??'');if(/^\s*[=+@-]/.test(x))x="'"+x;return '"'+x.replace(/"/g,'""')+'"'};let data=[keys.map(q).join(','),...rows.map(r=>keys.map(k=>q(r[k])).join(','))].join('\r\n');let u=URL.createObjectURL(new Blob([data],{type:'text/csv;charset=utf-8'}));let a=document.createElement('a');a.href=u;a.download='netforge-'+c+'.csv';a.click();setTimeout(()=>URL.revokeObjectURL(u),1000)}
function operations(){let c='outages';let el=document.querySelector('#content');el.insertAdjacentHTML('beforeend','<h2>Network operations</h2><div class="tabs" id="ops-tabs"></div><div id="ops-body"></div>');function draw(){document.querySelector('#ops-tabs').innerHTML=['outages','maintenance','inventory','leads','changes','ipam'].map(x=>'<button data-c="'+x+'" class="'+(x===c?'active':'')+'">'+esc(specs[x].title)+'</button>').join('');document.querySelectorAll('#ops-tabs button').forEach(b=>b.onclick=()=>{c=b.dataset.c;draw()});let rows=all(c);document.querySelector('#ops-body').innerHTML='<div class="toolbar"><span>'+rows.length+' records</span><button class="primary" id="ops-add">+ Add</button></div>'+table(c,specs[c].fields.slice(0,6).map(f=>f[0]),rows);document.querySelector('#ops-add').onclick=()=>form(c);document.querySelector('#ops-export').onclick=()=>exportCollection(c);document.querySelectorAll('#ops-body tr[data-id]').forEach(tr=>tr.onclick=()=>form(c,rows.find(x=>x.id===tr.dataset.id)))}draw()}

function workspaceTable(target,c,keys,filter){
 const rows=all(c).filter(filter||(()=>true));
 target.innerHTML='<div class="toolbar"><div><b>'+esc(specs[c].title)+'</b><div class="nf-detail-subtitle">'+rows.length+' records</div></div><div class="tools"><button id="workspace-export">Export CSV</button>'+(state.role!=='viewer'?'<button class="primary" id="workspace-add">+ New</button>':'')+'</div></div>'+table(c,keys,rows);
 target.querySelector('#workspace-export').onclick=()=>exportCollection(c);
 const b=target.querySelector('#workspace-add');if(b)b.onclick=()=>form(c);
 target.querySelectorAll('tr[data-id]').forEach(tr=>{tr.onclick=()=>detail(c,tr.dataset.id);tr.onkeydown=e=>{if(e.key==='Enter')detail(c,tr.dataset.id)}});
}
function finance(){
 const el=document.querySelector('#content'),invoices=all('invoices'),payments=all('payments');
 const toNum=x=>Number(x)||0;
 const total=invoices.reduce((n,i)=>n+toNum(i.amount),0),received=payments.reduce((n,p)=>n+toNum(p.amount),0);
 const assigned=invoices.reduce((n,i)=>n+Math.min(toNum(i.amount),payments.filter(p=>p.invoice_id===i.id).reduce((x,p)=>x+toNum(p.amount),0)),0);
 const balance=Math.max(0,total-assigned);
 const money=n=>new Intl.NumberFormat('en-ZA',{style:'currency',currency:'ZAR'}).format(n);
 const overdue=invoices.filter(i=>i.due_date&&i.due_date<dateKey(new Date())&&toNum(i.amount)>payments.filter(p=>p.invoice_id===i.id).reduce((x,p)=>x+toNum(p.amount),0)).length;
 el.insertAdjacentHTML('beforeend','<div class="nf-kicker">Financial administration</div><h2>Finance</h2><p class="muted">Manual invoice and payment register only. This is not an automated billing ledger or tax-compliant invoice engine.</p><div class="grid">'+[['Recorded invoices',money(total)],['Recorded payments',money(received)],['Unallocated invoice balance',money(balance)],['Overdue invoice records',overdue]].map(([k,v])=>'<div class="card nf-metric"><small>'+esc(k)+'</small><div class="metric" style="font-size:25px">'+esc(v)+'</div></div>').join('')+'</div><div class="tabs" id="finance-tabs"></div><div id="finance-body" class="section"></div>');
 let active='invoices',spec={invoices:['number','name','customer_id','due_date','amount','status'],payments:['reference','name','customer_id','invoice_id','amount','payment_date'],services:['name','customer_id','service_type','package','status','highsite_id','sector_id'],packages:['name','speed','price','download_speed','upload_speed']};
 function draw(){
  document.querySelector('#finance-tabs').innerHTML=Object.keys(spec).map(x=>'<button data-fin="'+x+'" class="'+(x===active?'active':'')+'">'+esc(specs[x].title)+'</button>').join('');
  document.querySelectorAll('#finance-tabs button').forEach(b=>b.onclick=()=>{active=b.dataset.fin;draw()});
  workspaceTable(document.querySelector('#finance-body'),active,spec[active]);
 }
 draw();
}
function wisp(){
 const el=document.querySelector('#content'),sites=all('highsites'),sectors=all('sectors'),customers=all('customers'),links=all('backhauls'),outages=all('outages');
 const active=customers.filter(x=>x.account_status!=='cancelled'),rfs=all('link_checks');
 const weak=active.filter(x=>x.signal_dbm!==''&&x.signal_dbm!=null&&Number.isFinite(Number(x.signal_dbm))&&Number(x.signal_dbm)<-75);
 const over=sectors.filter(x=>Number(x.max_clients)>0&&active.filter(c=>c.sector_id===x.id).length>Number(x.max_clients));
 const offline=sites.filter(x=>x.site_status==='offline');
 const alerts=[];
 for(const x of over){let n=active.filter(c=>c.sector_id===x.id).length;alerts.push({label:x.name+' over planned client capacity',sub:n+' clients / '+x.max_clients+' configured limit',view:'sectors'})}
 for(const x of offline)alerts.push({label:x.name+' marked offline',sub:'Based on manually recorded site status',view:'highsites'});
 for(const x of weak.slice(0,12))alerts.push({label:(x.account||x.name)+' weak recorded signal',sub:x.signal_dbm+' dBm · manually entered; threshold -75 dBm',view:'customers'});
 // Exact channel coincidence is a planning indicator, not an RF interference measurement.
 const rfGroups={};
 for(const x of sectors){let k=x.highsite_id+'|'+x.frequency+'|'+x.channel_width;if(x.highsite_id&&x.frequency)(rfGroups[k]??=[]).push(x)}
 for(const group of Object.values(rfGroups))if(group.length>1)alerts.push({label:'Coincident configured frequency: '+group.map(x=>x.name).join(', '),sub:'Same site, frequency and width — review channel reuse',view:'sectors'});
 el.insertAdjacentHTML('beforeend','<div class="nf-kicker">Wireless engineering</div><div class="nf-pagehead"><div><h2>WISP intelligence</h2><div class="nf-subtitle">Calculated from stored plans and manual observations, not live device telemetry</div></div><button id="survey-quick" class="primary">+ RF site survey</button></div><div class="grid">'+[['Highsites',sites.length],['Access sectors',sectors.length],['Backhaul links',links.length],['Wireless subscribers',active.filter(x=>x.service_type!=='fibre').length],['Sectors over limit',over.length],['Weak recorded links',weak.length],['RF snapshots',rfs.length],['Active site incidents',outages.filter(x=>x.status!=='resolved').length]].map(([k,v])=>'<div class="card nf-metric"><small>'+esc(k)+'</small><div class="metric">'+v+'</div></div>').join('')+'</div><div class="nf-grid-two section"><div class="card"><h3>Sector loading</h3><div id="wisp-sectors"></div></div><div class="card"><h3>Planning and service alerts</h3><div id="wisp-alerts"></div></div></div><div class="card section"><h3>Highsite subscriber impact preview</h3><p class="nf-detail-subtitle">Subscribers assigned directly to each site; use this for outage triage, not live availability claims.</p><div id="wisp-sites"></div></div>');
 document.querySelector('#survey-quick').onclick=()=>form('surveys',null,{status:'requested',los_status:'unknown'});
 document.querySelector('#wisp-sectors').innerHTML=sectors.length?sectors.map(x=>{
   let n=active.filter(c=>c.sector_id===x.id).length,max=Number(x.max_clients)||0,pct=max?Math.min(100,Math.round(100*n/max)):0;
   return '<div class="nf-statline" style="display:block"><div class="toolbar" style="margin:0"><b>'+esc(x.name)+'</b><small>'+n+(max?' / '+max:' subscribers')+' · '+esc(lookup('highsites',x.highsite_id))+'</small></div>'+(max?'<div class="nf-progress"><span style="width:'+pct+'%;background:'+(pct>=95?'#e15b5b':'#7954d7')+'"></span></div>':'')+'</div>';
 }).join(''):'<p class="nf-empty">Add highsites and access sectors to start tracking subscriber distribution.</p>';
 document.querySelector('#wisp-alerts').innerHTML=alerts.length?alerts.slice(0,20).map(a=>'<div class="nf-statline"><div><b>'+esc(a.label)+'</b><div class="nf-detail-subtitle">'+esc(a.sub)+'</div></div><button data-jump="'+esc(a.view)+'">View</button></div>').join(''):'<p class="nf-empty">No configured thresholds or manual status flags currently indicate issues.</p>';
 document.querySelectorAll('#wisp-alerts [data-jump]').forEach(b=>b.onclick=()=>go(b.dataset.jump));
 document.querySelector('#wisp-sites').innerHTML=sites.length?sites.map(site=>{
    let served=active.filter(c=>c.highsite_id===site.id).length,affected=outages.some(o=>o.highsite_id===site.id&&o.status!=='resolved');
    return '<div class="nf-statline"><div><b>'+esc(site.name)+'</b><div class="nf-detail-subtitle">'+esc(lookup('branches',site.branch_id))+'</div></div><div class="tools"><span class="nf-status '+(affected?'nf-bad':'nf-neutral')+'">'+served+' linked customers'+(affected?' · incident recorded':'')+'</span><button data-site="'+esc(site.id)+'">Open site</button></div></div>';
 }).join(''):'<p class="nf-empty">No highsites available.</p>';

 document.querySelectorAll('[data-site]').forEach(b=>b.onclick=()=>site360(b.dataset.site));
 el.insertAdjacentHTML('beforeend','<div class="nf-grid-two section"><div class="card"><h3>RF link budget and Fresnel calculator</h3><p class="nf-detail-subtitle">Free-space estimate only; not a substitute for terrain, clutter or interference modelling.</p><div class="formgrid"><div><label>Distance (km)</label><input id="rf-distance" type="number" min="0.001" step="0.1" value="5"></div><div><label>Frequency (GHz)</label><input id="rf-frequency" type="number" min="0.001" step="0.1" value="5.8"></div><div><label>TX power (dBm)</label><input id="rf-tx" type="number" step="1" value="20"></div><div><label>TX antenna gain (dBi)</label><input id="rf-txgain" type="number" step="1" value="20"></div><div><label>RX antenna gain (dBi)</label><input id="rf-rxgain" type="number" step="1" value="20"></div><div><label>Total cable/connector losses (dB)</label><input id="rf-loss" type="number" step="0.5" min="0" value="2"></div><div><label>Effective Earth k-factor</label><input id="rf-k" type="number" step="0.01" min="0.1" value="1.333"></div></div><div id="rf-results" class="nf-sidecard section" role="status"></div></div><div class="card"><h3>Coordinate-based backhaul topology</h3><p class="nf-detail-subtitle">Schematic only, positioned from entered highsite GPS coordinates. Click a site to view its records.</p><div id="wisp-topology"></div></div></div>');
 const vals=['rf-distance','rf-frequency','rf-tx','rf-txgain','rf-rxgain','rf-loss','rf-k'];
 const compute=()=>{
    const [d,f,tx,gTx,gRx,loss,k]=vals.map(x=>Number(document.getElementById(x)?.value));
    const output=document.querySelector('#rf-results');
    if(!Number.isFinite(d)||!Number.isFinite(f)||!Number.isFinite(k)||d<=0||f<=0||k<=0||d>500||f>120||!Number.isFinite(tx)||!Number.isFinite(gTx)||!Number.isFinite(gRx)||!Number.isFinite(loss)||loss<0){
       output.textContent='Enter valid link parameters; distance must be 0–500 km and frequency 0–120 GHz.';return;
    }
    const fspl=92.45+20*Math.log10(d)+20*Math.log10(f);
    const receive=tx+gTx+gRx-loss-fspl;
    const firstZone=8.66*Math.sqrt(d/f);
    const bulge=d*d/(51*k);
    output.innerHTML=nfProperty('Free-space path loss',fspl.toFixed(1)+' dB')+nfProperty('Estimated received power',receive.toFixed(1)+' dBm')+nfProperty('First Fresnel zone radius at midpoint',firstZone.toFixed(2)+' m')+nfProperty('60% zone clearance radius',(firstZone*.6).toFixed(2)+' m')+nfProperty('Earth curvature bulge at midpoint',bulge.toFixed(2)+' m');
 };
 vals.forEach(x=>document.getElementById(x).addEventListener('input',compute));compute();
 const withGPS=sites.filter(x=>x.latitude!==''&&x.longitude!==''&&Number.isFinite(Number(x.latitude))&&Number.isFinite(Number(x.longitude))&&Math.abs(Number(x.latitude))<=90&&Math.abs(Number(x.longitude))<=180);
 const topology=document.querySelector('#wisp-topology');
 if(!withGPS.length){topology.innerHTML='<p class="nf-empty">Enter GPS coordinates on highsite records to build the topology.</p>';}
 else{
   const lon=withGPS.map(x=>Number(x.longitude)),lat=withGPS.map(x=>Number(x.latitude));
   const left=Math.min(...lon),right=Math.max(...lon),bottom=Math.min(...lat),top=Math.max(...lat);
   const margin=45,width=860,height=320;
   const pos=x=>({x:margin+(Number(x.longitude)-left)/Math.max(right-left,0.02)*(width-2*margin),y:height-margin-(Number(x.latitude)-bottom)/Math.max(top-bottom,0.02)*(height-2*margin)});
   let svg='<svg role="img" aria-label="Schematic highsite positions and planned backhaul connections" viewBox="0 0 860 320" style="width:100%;height:auto;background:#fbfaff;border-radius:8px;border:1px solid #e8e7ef">';
   for(const link of links){const source=withGPS.find(x=>x.id===link.highsite_id),target=withGPS.find(x=>x.id===link.remote_highsite_id);if(!source||!target)continue;let a=pos(source),b=pos(target);svg+='<line x1="'+a.x+'" y1="'+a.y+'" x2="'+b.x+'" y2="'+b.y+'" stroke="#9180d2" stroke-width="3" stroke-linecap="round"><title>'+esc(link.name)+'</title></line>';}
   for(const site of withGPS){let p=pos(site),served=active.filter(c=>c.highsite_id===site.id).length;svg+='<circle data-topology-site="'+esc(site.id)+'" cx="'+p.x+'" cy="'+p.y+'" r="10" fill="#7852d7" stroke="white" stroke-width="3" style="cursor:pointer"><title>'+esc(site.name)+' ('+served+' customers)</title></circle><text x="'+(p.x+14)+'" y="'+(p.y-10)+'" fill="#3d3c56" font-size="13">'+esc(site.name.slice(0,22))+'</text>';}
   svg+='</svg>';
   topology.innerHTML=svg+'<p class="nf-detail-subtitle">'+withGPS.length+' GPS-enabled sites. Lines represent configured backhaul records, not measured network links.</p>';
   topology.querySelectorAll('[data-topology-site]').forEach(x=>x.onclick=()=>site360(x.dataset.topologySite));
 }
}
function nfRows(rows,fields,empty='No linked records.'){
 return rows.length?'<div class="card tablewrap"><table><thead><tr>'+fields.map(([k,l])=>'<th>'+esc(l)+'</th>').join('')+'</tr></thead><tbody>'+rows.map(r=>'<tr data-record="'+esc(r.id)+'">'+fields.map(([k])=>'<td>'+esc(resolvedValue(k,r))+'</td>').join('')+'</tr>').join('')+'</tbody></table></div>':'<p class="nf-empty">'+esc(empty)+'</p>';
}
function nfProperty(k,v){return '<div class="nf-statline"><span class="muted">'+esc(k)+'</span><span class="nf-stat-value">'+esc(v||'—')+'</span></div>'}
function customer360(id){
 let r=all('customers').find(x=>x.id===id);if(!r)return;
 let d=dialog('<div class="nf-pagehead"><div><div class="nf-kicker">Customer 360°</div><h2>'+esc(r.account||r.name+' '+(r.surname||''))+'</h2><div class="nf-detail-subtitle">'+esc(r.name+' '+r.surname)+' · '+esc(lookup('branches',r.branch_id))+'</div></div><div class="nf-quick-actions"><span class="nf-status nf-neutral">'+esc(r.account_status||'unclassified')+'</span><button id="nf-edit">Edit</button><button id="nf-close">Close</button></div></div><div class="tabs nf-detail-tabs" id="nf-profile-tabs"></div><div id="nf-profile-content"></div>');
 d.classList.add('nf-customer-modal');
 d.querySelector('#nf-close').onclick=()=>d.close();
 d.querySelector('#nf-edit').onclick=()=>{d.close();form('customers',r)};
 let current='Overview';
 const related={
  equipment:all('equipment').filter(x=>x.customer_id===id),
  tickets:all('tickets').filter(x=>x.customer_id===id),
  services:all('services').filter(x=>x.customer_id===id),
  activities:all('activities').filter(x=>x.customer_id===id),
  link_checks:all('link_checks').filter(x=>x.customer_id===id),
  invoices:all('invoices').filter(x=>x.customer_id===id),
  payments:all('payments').filter(x=>x.customer_id===id)
 };
 function quick(c,label,defaults={}){return '<button data-new="'+c+'">'+esc(label)+'</button>'}
 function draw(){
  const panels=['Overview','Services','Equipment','Tickets','Activity','RF diagnostics','Billing'];
  d.querySelector('#nf-profile-tabs').innerHTML=panels.map(x=>'<button data-panel="'+x+'" class="'+(x===current?'active':'')+'">'+x+'</button>').join('');
  d.querySelectorAll('[data-panel]').forEach(b=>b.onclick=()=>{current=b.dataset.panel;draw()});
  const area=d.querySelector('#nf-profile-content'),site=all('highsites').find(x=>x.id===r.highsite_id),sector=all('sectors').find(x=>x.id===r.sector_id);
  const activeOutage=all('outages').some(x=>x.highsite_id===r.highsite_id&&x.status!=='resolved');
  if(current==='Overview'){
   let coords=r.latitude&&r.longitude&&Number.isFinite(Number(r.latitude))&&Number.isFinite(Number(r.longitude))?'<a rel="noopener noreferrer" target="_blank" href="https://www.openstreetmap.org/?mlat='+encodeURIComponent(r.latitude)+'&mlon='+encodeURIComponent(r.longitude)+'#map=15/'+encodeURIComponent(r.latitude)+'/'+encodeURIComponent(r.longitude)+'">Open location map</a>':'No GPS position saved';
   area.innerHTML='<div class="nf-quick-actions" style="margin-bottom:14px">'+quick('tickets','New ticket')+quick('activities','Log call')+quick('workorders','Work order')+quick('surveys','RF survey')+'</div><div class="nf-grid-two"><div class="card"><h3>Account information</h3>'+nfProperty('Account',r.account)+nfProperty('Name',r.name+' '+r.surname)+nfProperty('Phone',r.phone)+nfProperty('Email',r.email)+nfProperty('Address',r.address)+nfProperty('Package',r.package)+nfProperty('Service status',r.account_status)+nfProperty('Contact last recorded',r.last_contact)+'</div><div class="card"><h3>WISP service context</h3>'+nfProperty('Serving highsite',site?.name)+nfProperty('Access sector',sector?.name)+nfProperty('Configured frequency',sector?.frequency?sector.frequency+' MHz':'')+nfProperty('Azimuth',sector?.azimuth?sector.azimuth+'°':'')+nfProperty('CPE IP',r.cpe_ip)+nfProperty('PPPoE username',r.pppoe_username)+nfProperty('Recorded RSSI',r.signal_dbm?r.signal_dbm+' dBm':'')+nfProperty('Highsite incident',activeOutage?'Unresolved incident recorded':'No incident recorded')+'<div style="margin-top:13px">'+coords+'</div></div></div><div class="grid section">'+[['Service records',related.services.length],['Equipment items',related.equipment.length],['Tickets',related.tickets.length],['Contact events',related.activities.length],['RF snapshots',related.link_checks.length]].map(([k,n])=>'<div class="card"><small>'+esc(k)+'</small><div class="metric">'+n+'</div></div>').join('')+'</div>';
  }else if(current==='Equipment'){
   area.innerHTML='<div class="toolbar"><h3>Customer-owned and ISP-provided equipment</h3>'+quick('equipment','+ Add device')+'</div><div class="tabs" id="nf-equip-tabs"></div><div id="nf-equip-body"></div>';
   let kind='cpe';
   const show=()=>{area.querySelector('#nf-equip-tabs').innerHTML=[['cpe','CPE / antennas'],['router','Internal routers'],['other','Other purchases']].map(([k,l])=>'<button data-kind="'+k+'" class="'+(kind===k?'active':'')+'">'+l+'</button>').join('');
   area.querySelectorAll('[data-kind]').forEach(b=>b.onclick=()=>{kind=b.dataset.kind;show()});
   const devices=related.equipment.filter(x=>x.category===kind);
   area.querySelector('#nf-equip-body').innerHTML=devices.length?devices.map(x=>'<div class="card" style="margin-bottom:8px"><div class="nf-statline"><div><b>'+esc(x.name)+'</b><div class="nf-detail-subtitle">'+esc(x.type)+' · '+esc(x.ip||'No IP')+' · '+esc(x.serial_number||'No serial')+'</div><div class="nf-detail-subtitle">'+esc(x.ownership||'Ownership unspecified')+'</div></div><div class="tools"><button data-edit-device="'+esc(x.id)+'">Edit</button><button data-device-access="'+esc(x.id)+'">Connect</button></div></div></div>').join(''):'<p class="nf-empty">No devices recorded for this category.</p>';
   area.querySelectorAll('[data-edit-device]').forEach(b=>b.onclick=()=>{d.close();form('equipment',related.equipment.find(x=>x.id===b.dataset.editDevice))});
   area.querySelectorAll('[data-device-access]').forEach(b=>b.onclick=()=>access(related.equipment.find(x=>x.id===b.dataset.deviceAccess)));
   };
   show();
  }else if(current==='Services'){
    area.innerHTML='<div class="toolbar"><h3>Services and connectivity</h3>'+quick('services','+ Add service')+'</div>'+nfRows(related.services,[['name','Service'],['service_type','Type'],['package','Package'],['status','Status'],['highsite_id','Highsite'],['sector_id','Sector'],['ip','IP']], 'No individual service records. The main customer account still contains legacy package fields.');
  }else if(current==='Tickets'){
   area.innerHTML='<div class="toolbar"><h3>Customer tickets</h3>'+quick('tickets','+ New ticket')+'</div>'+nfRows(related.tickets,[['number','Number'],['name','Subject'],['priority','Priority'],['status','Status'],['scheduled_date','Schedule']]);
  }else if(current==='Activity'){
   let acts=[...related.activities].sort((a,b)=>String(b.occurred_at).localeCompare(String(a.occurred_at)));
   area.innerHTML='<div class="toolbar"><h3>Calls and communication history</h3>'+quick('activities','+ Log interaction')+'</div>'+nfRows(acts,[['occurred_at','When'],['type','Type'],['channel','Channel'],['name','Subject'],['performed_by','Staff']]);
  }else if(current==='RF diagnostics'){
   let snapshots=[...related.link_checks].sort((a,b)=>String(b.captured_at).localeCompare(String(a.captured_at)));
   area.innerHTML='<div class="toolbar"><div><h3>Subscriber RF history</h3><small>Manually recorded snapshots; no live SNMP or radio API collection.</small></div>'+quick('link_checks','+ Record RF check')+'</div>'+nfRows(snapshots,[['captured_at','When'],['rssi_dbm','RSSI dBm'],['snr_db','SNR dB'],['ccq','CCQ %'],['tx_mbps','TX Mbps'],['rx_mbps','RX Mbps']]);
  }else if(current==='Billing'){
   const total=related.invoices.reduce((n,x)=>n+(Number(x.amount)||0),0),paid=related.payments.reduce((n,x)=>n+(Number(x.amount)||0),0);
   area.innerHTML='<div class="grid">'+[['Recorded invoices',total],['Recorded payments',paid],['Unreconciled difference',total-paid]].map(([k,v])=>'<div class="card"><small>'+k+'</small><div class="metric" style="font-size:25px">R '+v.toFixed(2)+'</div></div>').join('')+'</div><div class="toolbar section"><h3>Invoice register</h3>'+quick('invoices','+ Invoice')+'</div>'+nfRows(related.invoices,[['number','Number'],['amount','ZAR'],['due_date','Due'],['status','Status']])+'<div class="toolbar section"><h3>Payments</h3>'+quick('payments','+ Payment')+'</div>'+nfRows(related.payments,[['reference','Reference'],['amount','ZAR'],['payment_date','Date']]);
  }
  area.querySelectorAll('[data-new]').forEach(b=>b.onclick=()=>{let c=b.dataset.new,defaults={customer_id:id};if(c==='tickets')Object.assign(defaults,{type:'customer',name:'Customer support',status:'open',priority:'normal',branch_id:r.branch_id});if(c==='activities')Object.assign(defaults,{type:'call',channel:'phone',name:'Inbound call'});if(c==='equipment')Object.assign(defaults,{category:'cpe',protocol:'https',ownership:'isp-owned'});if(c==='services')Object.assign(defaults,{highsite_id:r.highsite_id,sector_id:r.sector_id,package:r.package,service_type:r.service_type||'wireless',status:'active'});if(c==='link_checks')Object.assign(defaults,{sector_id:r.sector_id});if(c==='workorders')Object.assign(defaults,{branch_id:r.branch_id});if(c==='surveys')Object.assign(defaults,{highsite_id:r.highsite_id,branch_id:r.branch_id,los_status:'unknown',status:'requested'});d.close();form(c,null,defaults)});
  area.querySelectorAll('tr[data-record]').forEach(tr=>tr.onclick=()=>{let map={Services:'services',Tickets:'tickets',Activity:'activities','RF diagnostics':'link_checks'},c=map[current]||(current==='Billing'?(related.invoices.some(x=>x.id===tr.dataset.record)?'invoices':'payments'):null);if(!c)return;let row=all(c).find(x=>x.id===tr.dataset.record);if(row){d.close();form(c,row)}});
 }
 draw();
}
function site360(id){
 const r=all('highsites').find(x=>x.id===id);if(!r)return;
 const d=dialog('<div class="nf-pagehead"><div><div class="nf-kicker">Network infrastructure</div><h2>'+esc(r.name)+'</h2><div class="nf-detail-subtitle">'+esc(r.number||'No site number')+' · '+esc(lookup('branches',r.branch_id))+'</div></div><div class="nf-quick-actions"><span class="nf-status nf-neutral">'+esc(r.site_status||'Unclassified')+'</span><button id="nf-edit">Edit site</button><button id="nf-close">Close</button></div></div><div class="tabs nf-detail-tabs" id="nf-site-tabs"></div><div id="nf-site-content"></div>');
 d.classList.add('nf-site-modal');d.querySelector('#nf-close').onclick=()=>d.close();d.querySelector('#nf-edit').onclick=()=>{d.close();form('highsites',r)};
 let current='Overview';
 const sectors=all('sectors').filter(x=>x.highsite_id===id),customers=all('customers').filter(x=>x.highsite_id===id),devices=all('equipment').filter(x=>x.highsite_id===id),links=all('backhauls').filter(x=>x.highsite_id===id||x.remote_highsite_id===id),outages=all('outages').filter(x=>x.highsite_id===id);
 function draw(){
  const panels=['Overview','Distribution','Backhaul','Access','Power','Customers','Outages'];
  d.querySelector('#nf-site-tabs').innerHTML=panels.map(x=>'<button data-panel="'+x+'" class="'+(x===current?'active':'')+'">'+x+'</button>').join('');
  d.querySelectorAll('[data-panel]').forEach(b=>b.onclick=()=>{current=b.dataset.panel;draw()});
  let a=d.querySelector('#nf-site-content');
  if(current==='Overview'){
   let coords=r.latitude&&r.longitude?'<a target="_blank" rel="noopener noreferrer" href="https://www.openstreetmap.org/?mlat='+encodeURIComponent(r.latitude)+'&mlon='+encodeURIComponent(r.longitude)+'#map=15/'+encodeURIComponent(r.latitude)+'/'+encodeURIComponent(r.longitude)+'">Map location</a>':'No GPS coordinates';
   a.innerHTML='<div class="grid">'+[['Customers served',customers.length],['Access sectors',sectors.length],['Backhaul links',links.length],['Onsite equipment',devices.length],['Open incidents',outages.filter(x=>x.status!=='resolved').length]].map(([k,v])=>'<div class="card"><small>'+esc(k)+'</small><div class="metric">'+v+'</div></div>').join('')+'</div><div class="nf-grid-two section"><div class="card"><h3>Highsite profile</h3>'+nfProperty('Site number',r.number)+nfProperty('Address',r.address)+nfProperty('Branch',lookup('branches',r.branch_id))+nfProperty('Tower height',r.tower_height?r.tower_height+' m':'')+nfProperty('Owner',r.site_owner)+nfProperty('Lease expiry',r.lease_expiry)+'<div style="margin-top:10px">'+coords+'</div></div><div class="card"><h3>Power and access</h3>'+nfProperty('Power',r.power_source)+nfProperty('Battery capacity',r.battery_capacity?r.battery_capacity+' Ah':'')+nfProperty('UPS runtime',r.ups_runtime?r.ups_runtime+' hours':'')+nfProperty('Generator',r.generator)+nfProperty('Next inspection',r.next_inspection)+nfProperty('Site contact',r.site_contact)+'</div></div>';
  }else if(current==='Access'){
   a.innerHTML='<div class="toolbar"><h3>Access sectors</h3><button class="primary" data-add-sector>+ Add sector</button></div>'+nfRows(sectors,[['name','Sector'],['ssid','SSID'],['azimuth','Azimuth'],['frequency','MHz'],['ip','Management IP'],['max_clients','Planned limit']]);
   a.querySelector('[data-add-sector]').onclick=()=>{d.close();form('sectors',null,{highsite_id:id,sector_status:'active',protocol:'https'})};
  }else if(current==='Customers'){
   a.innerHTML='<h3>Served subscribers ('+customers.length+')</h3>'+nfRows(customers,[['account','Account'],['name','First name'],['surname','Surname'],['package','Package'],['sector_id','Sector']]);
  }else if(current==='Outages'){
   a.innerHTML='<div class="toolbar"><h3>Site incidents</h3><button class="primary" data-add-outage>+ Log incident</button></div><div class="notice">Potential impact: '+customers.length+' customer accounts explicitly assigned to this highsite.</div>'+nfRows(outages,[['name','Incident'],['status','Status'],['severity','Severity'],['start_date','Opened']]);
   a.querySelector('[data-add-outage]').onclick=()=>{d.close();form('outages',null,{highsite_id:id,status:'investigating'})};
  }else{
   let category=current.toLowerCase(),dev=devices.filter(x=>x.category===category);
   a.innerHTML='<div class="toolbar"><div><h3>'+current+' equipment</h3><small>'+dev.length+' devices registered</small></div><div class="tools"><button class="primary" data-add-device>+ Add device</button>'+(current==='Backhaul'?'<button data-add-link>+ Backhaul link</button>':'')+'</div></div>'+dev.map(x=>'<div class="card" style="margin-bottom:8px"><div class="nf-statline"><div><b>'+esc(x.name)+'</b><div class="nf-detail-subtitle">'+esc(x.type)+' · '+esc(x.ip||'No management IP')+'</div></div><div class="tools"><button data-manage="'+esc(x.id)+'">Edit</button><button data-access="'+esc(x.id)+'">Connect</button></div></div></div>').join('')+(dev.length?'':'<p class="nf-empty">No equipment recorded.</p>')+(current==='Backhaul'?'<h3 class="section">Linked backhauls</h3>'+nfRows(links,[['name','Link'],['frequency','MHz'],['link_capacity','Capacity Mbps'],['target_rssi','Target RSSI']]):'');
   a.querySelector('[data-add-device]').onclick=()=>{d.close();form('equipment',null,{highsite_id:id,category,protocol:'https'})};
   const link=a.querySelector('[data-add-link]');if(link)link.onclick=()=>{d.close();form('backhauls',null,{highsite_id:id})};
   a.querySelectorAll('[data-manage]').forEach(b=>b.onclick=()=>{d.close();form('equipment',dev.find(x=>x.id===b.dataset.manage))});
   a.querySelectorAll('[data-access]').forEach(b=>b.onclick=()=>access(dev.find(x=>x.id===b.dataset.access)));
  }
  a.querySelectorAll('tr[data-record]').forEach(tr=>tr.onclick=()=>{let c=current==='Access'?'sectors':current==='Customers'?'customers':current==='Outages'?'outages':current==='Backhaul'?'backhauls':'equipment';let record=all(c).find(x=>x.id===tr.dataset.record);if(record){d.close();if(c==='customers')customer360(record.id);else form(c,record)}});
 }
 draw();
}
function showGlobalSearch(){
 const d=dialog('<h2>Search NetForge</h2><p class="nf-detail-subtitle">Find accounts, ticket numbers, highsites, equipment IPs and sectors.</p><input id="quick-search" autofocus placeholder="Start typing..." aria-label="Search all records"><div id="search-results" style="max-height:60vh;overflow:auto;margin-top:12px"></div><div style="text-align:right;margin-top:12px"><button id="search-close">Close</button></div>');
 d.querySelector('#search-close').onclick=()=>d.close();const input=d.querySelector('#quick-search');input.focus();
 const index=[['customers',['account','name','surname','address','phone','cpe_ip']],['tickets',['number','name','description']],['highsites',['name','number','address']],['sectors',['name','ssid','ip','frequency']],['equipment',['name','ip','serial_number','mac']],['ipam',['name','address','vlan']],['leads',['name','phone']]];
 const draw=()=>{
   const q=input.value.trim().toLowerCase(),target=d.querySelector('#search-results');
   if(q.length<2){target.innerHTML='<p class="nf-empty">Enter at least two characters.</p>';return}
   const hits=index.flatMap(([c,keys])=>all(c).filter(x=>keys.some(k=>String(x[k]||'').toLowerCase().includes(q))).map(x=>({c,x}))).slice(0,30);
   target.innerHTML=hits.length?hits.map(({c,x},i)=>'<button class="nf-search-result" data-hit="'+i+'"><span><b>'+esc(x.account||x.name||x.number||x.address)+'</b><br><small>'+esc(x.number||x.ip||x.address||'')+'</small></span><span class="nf-status nf-neutral">'+esc(c)+'</span></button>').join(''):'<p class="nf-empty">No matching records.</p>';
   target.querySelectorAll('[data-hit]').forEach(b=>b.onclick=()=>{let item=hits[Number(b.dataset.hit)];d.close();let route={equipment:'equipment',ipam:'ipam',leads:'leads'}[item.c]||item.c;go(route);detail(item.c,item.x.id)});
 };
 input.oninput=draw;input.onkeydown=e=>{if(e.key==='Enter')d.querySelector('[data-hit="0"]')?.click()};draw();
}
document.querySelector('#global-search').onclick=showGlobalSearch;
document.addEventListener('keydown',e=>{if(e.key==='/'&&!['INPUT','TEXTAREA','SELECT'].includes(document.activeElement?.tagName)){e.preventDefault();showGlobalSearch()}});

document.querySelector('#logout').onclick=async()=>{try{await call({action:'logout'},'POST');await boot()}catch(e){alert(e.message)}};boot();

