# Loads a Marshal file and checks that arr[0] and arr[1] are the same
# object (identity, not just equal value) after Marshal.load — i.e. that
# the file's link table correctly round-trips through real Ruby.
# Prints PASS or FAIL and exits 0/1 accordingly.
#
# Fixture classes matching MarshalTests.cs's WriterModelFactories, needed
# because Marshal.load resolves class/module names to real constants.
class MyU
  def marshal_load(v)
    @v = v
  end
end

module Ext
  def foo; end
end

class MyArr < Array
end

data = File.binread(ARGV[0])
arr = Marshal.load(data)

unless arr.is_a?(Array) && arr.length == 2
  puts "FAIL: expected a 2-element Array, got #{arr.class}"
  exit 1
end

if arr[0].equal?(arr[1])
  puts "PASS"
  exit 0
else
  puts "FAIL: arr[0] and arr[1] are not the same object (#{arr[0].object_id} vs #{arr[1].object_id})"
  exit 1
end
