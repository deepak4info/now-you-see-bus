import { useMemo, useState } from "react";
import { Link } from "react-router-dom";
import { ArrowUpRight, MapPin, Phone, QrCode, Search, X } from "lucide-react";

type BusStandContact = {
  district: string;
  stand: string;
  numbers: string[];
};

const contacts: BusStandContact[] = [
  { district: "Ambala", stand: "Ambala City", numbers: ["0171-2556388"] },
  { district: "Ambala", stand: "Ambala Cantt.", numbers: ["0171-2640821"] },
  { district: "Ambala", stand: "Naraingarh", numbers: ["01734-284038"] },
  { district: "Bhiwani", stand: "Bhiwani", numbers: ["01664-242230"] },
  { district: "Charkhi Dadri", stand: "Charkhi Dadri", numbers: ["01250-220144"] },
  { district: "Charkhi Dadri", stand: "Loharu", numbers: ["01252-258207"] },
  { district: "Faridabad", stand: "Faridabad", numbers: ["0129-2244953"] },
  { district: "Fatehabad", stand: "Fatehabad", numbers: ["01667-220617"] },
  { district: "Fatehabad", stand: "Tohana", numbers: ["01692-220036"] },
  { district: "Gurgaon", stand: "Gurgaon", numbers: ["0124-2320222", "0124-4912626", "9306068221"] },
  { district: "Hisar", stand: "Hisar", numbers: ["01662-233285"] },
  { district: "Hisar", stand: "Hansi", numbers: ["01663-254081"] },
  { district: "Jind", stand: "Jind", numbers: ["01681-245370"] },
  { district: "Jind", stand: "Narwana", numbers: ["01684-240104"] },
  { district: "Jind", stand: "Safidon", numbers: ["01686-262235"] },
  { district: "Jhajjar", stand: "Jhajjar", numbers: ["94671-54214"] },
  { district: "Jhajjar", stand: "Bahadurgarh", numbers: ["94671-54209"] },
  { district: "Jhajjar", stand: "New Bus Stand", numbers: ["09467154214"] },
  { district: "Kaithal", stand: "Kaithal", numbers: ["01746-224214"] },
  { district: "Karnal", stand: "Karnal Old Bus Stand", numbers: ["0184-2251158"] },
  { district: "Kurukshetra", stand: "Kurukshetra", numbers: ["01744-220468"] },
  { district: "Kurukshetra", stand: "Pehowa", numbers: ["01741-220102"] },
  { district: "Mohindergarh", stand: "Mohindergarh", numbers: ["01285-222100"] },
  { district: "Mohindergarh", stand: "Narnaul", numbers: ["01282-251947"] },
  { district: "Panipat", stand: "Panipat", numbers: ["0180-2646544"] },
  { district: "Panchkula", stand: "Panchkula", numbers: ["0172-2562200"] },
  { district: "Panchkula", stand: "Chandigarh", numbers: ["0172-2704014"] },
  { district: "Panchkula", stand: "Kalka", numbers: ["01733-225125"] },
  { district: "Rohtak", stand: "Rohtak", numbers: ["01262-276641"] },
  { district: "Rewari", stand: "Rewari", numbers: ["01274-256751"] },
  { district: "Sonipat", stand: "Sonipat", numbers: ["0130-2201101"] },
  { district: "Sonipat", stand: "Gohana", numbers: ["01263-252140"] },
  { district: "Sirsa", stand: "Sirsa", numbers: ["01666-220866"] },
  { district: "Sirsa", stand: "Dabwali", numbers: ["01668-226115"] },
  { district: "Yamunanagar", stand: "Yamunanagar", numbers: ["01732-227717"] },
  { district: "Mewat (Nuh)", stand: "Nuh", numbers: ["01267-274714"] },
  { district: "Palwal", stand: "Palwal", numbers: ["01275-240285"] },
  { district: "Delhi", stand: "ISBT-Delhi, Kashmere Gate", numbers: ["011-42161053"] },
  { district: "Chandigarh", stand: "ISBT-17, Chd.", numbers: ["0172-2704014"] },
];

export function EnquiryNumbersPage() {
  const [query, setQuery] = useState("");
  const filteredContacts = useMemo(() => {
    const normalizedQuery = query.trim().toLocaleLowerCase();
    if (!normalizedQuery) return contacts;
    return contacts.filter(({ district, stand, numbers }) =>
      [district, stand, ...numbers].some((value) => value.toLocaleLowerCase().includes(normalizedQuery)),
    );
  }, [query]);
  const districtsCount = new Set(contacts.map(({ district }) => district)).size;

  return (
    <div className="mx-auto max-w-6xl px-4 pb-12 pt-8 md:pb-16 md:pt-12">
      <header className="max-w-3xl">
        <span className="inline-flex items-center gap-2 text-xs font-semibold uppercase tracking-[0.18em] text-primary">
          <span className="pulse" /> Haryana Roadways · Passenger information
        </span>
        <h1 className="mt-2 font-display text-4xl leading-tight tracking-tight text-ink sm:text-5xl">
          Bus stand enquiry numbers
        </h1>
        <p className="mt-3 text-sm leading-6 text-muted-foreground sm:text-base">
          Find and call a Haryana Roadways bus stand for service information.
        </p>
      </header>

      <section className="mt-7 rounded-2xl border border-border/70 bg-card p-4 shadow-sm sm:p-5" aria-labelledby="bus-stand-directory">
        <div className="flex flex-wrap items-end justify-between gap-3">
          <div>
            <h2 id="bus-stand-directory" className="font-display text-2xl text-ink">Enquiry directory</h2>
            <p className="mt-1 text-sm text-muted-foreground">{contacts.length} bus stands · {districtsCount} districts</p>
          </div>
          <a
            href="https://hartrans.gov.in/bus-stand-enquiry-numbers/"
            target="_blank"
            rel="noreferrer"
            className="inline-flex items-center gap-1 text-sm font-medium text-primary hover:underline"
          >
            Official source <ArrowUpRight aria-hidden="true" className="h-4 w-4" />
          </a>
        </div>

        <label htmlFor="bus-stand-search" className="relative mt-5 block">
          <Search aria-hidden="true" className="absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground" />
          <input
            id="bus-stand-search"
            type="search"
            value={query}
            onChange={(event) => setQuery(event.target.value)}
            placeholder="Search by district, bus stand, or phone number"
            className="h-11 w-full rounded-xl border border-border bg-background pl-10 pr-10 text-sm outline-none transition-colors placeholder:text-muted-foreground focus-visible:border-primary/50 focus-visible:ring-2 focus-visible:ring-primary/15"
          />
          {query && (
            <button
              type="button"
              onClick={() => setQuery("")}
              aria-label="Clear search"
              className="absolute right-3 top-1/2 -translate-y-1/2 rounded p-1 text-muted-foreground hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
            >
              <X aria-hidden="true" className="h-4 w-4" />
            </button>
          )}
        </label>

        {filteredContacts.length > 0 ? (
          <div className="mt-4 overflow-hidden rounded-xl border border-border/70">
            <div className="hidden grid-cols-[minmax(0,1fr)_minmax(0,1.5fr)_minmax(0,1fr)] gap-4 bg-muted/50 px-4 py-3 text-xs font-semibold uppercase tracking-wide text-muted-foreground sm:grid">
              <span>District</span><span>Bus stand</span><span>Enquiry number</span>
            </div>
            <ul className="divide-y divide-border/70">
              {filteredContacts.map(({ district, stand, numbers }) => (
                <li key={`${district}-${stand}`} className="grid gap-2 px-4 py-4 sm:grid-cols-[minmax(0,1fr)_minmax(0,1.5fr)_minmax(0,1fr)] sm:items-center sm:gap-4">
                  <span className="inline-flex items-center gap-2 text-xs font-medium text-muted-foreground sm:text-sm">
                    <MapPin aria-hidden="true" className="h-4 w-4 shrink-0 text-primary sm:hidden" />
                    {district}
                  </span>
                  <div className="flex items-center gap-2 flex-wrap">
                    <span className="font-semibold text-ink">{stand}</span>
                    <Link
                      to={`/stop/${encodeURIComponent(stand)}`}
                      className="inline-flex items-center gap-1 rounded-md bg-primary/10 px-2 py-0.5 text-[11px] font-semibold text-primary hover:bg-primary/20 transition-colors"
                      title={`Open live timetable board for ${stand}`}
                    >
                      <QrCode size={11} /> Live Board ↗
                    </Link>
                  </div>
                  <div className="flex flex-wrap gap-x-4 gap-y-2">
                    {numbers.map((number) => (
                      <a
                        key={number}
                        href={`tel:${number.replace(/[^\d+]/g, "")}`}
                        className="inline-flex items-center gap-2 text-sm font-semibold text-primary hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
                      >
                        <Phone aria-hidden="true" className="h-4 w-4" />
                        <span>{number}</span>
                      </a>
                    ))}
                  </div>
                </li>
              ))}
            </ul>
          </div>
        ) : (
          <div className="mt-4 rounded-xl border border-dashed border-border px-4 py-10 text-center">
            <p className="font-semibold text-ink">No bus stands found</p>
            <p className="mt-1 text-sm text-muted-foreground">Try another district, stand name, or phone number.</p>
          </div>
        )}

        <p className="mt-4 text-xs leading-5 text-muted-foreground">
          Contact numbers are listed as published by Haryana Transport. Please confirm availability with the bus stand.
        </p>
      </section>
    </div>
  );
}
